using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Oracle.ManagedDataAccess.Client;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace OracleTests.Transport;

/// <summary>
/// GH-5411. An envelope the inbox already holds -- handled earlier after being received at another address --
/// used to fail the durable pop's inbox INSERT with ORA-00001, so the whole batch rolled back onto the queue on
/// every poll. Now the duplicate leaves the queue and is discarded, and the rest is received.
/// </summary>
[Collection("oracle")]
public class Bug_5411_durable_pop_skips_envelopes_the_inbox_holds
{
    private static readonly Uri ElsewhereAddress = new("oracle://elsewhere/");

    [Fact]
    public async Task a_duplicate_does_not_hold_up_the_rest_of_the_batch()
    {
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await using (var conn = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            foreach (var table in new[] { "WOLVERINE_QUEUE_GH5411", "WOLVERINE_QUEUE_GH5411_SCHEDULED" })
            {
                try
                {
                    await using var cmd = conn.CreateCommand($"DROP TABLE WOLVERINE.{table} CASCADE CONSTRAINTS");
                    await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                catch (OracleException)
                {
                    // not there yet
                }
            }
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE").EnableMessageTransport();
                opts.Durability.Mode = DurabilityMode.Solo;

                // Declared without a listener: the test pops by hand
                opts.PublishMessage<Bug5411Message>().ToOracleQueue("gh5411");
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var queue = runtime.Options.Transports.GetOrCreate<OracleTransport>().Queues["gh5411"];
        await queue.PurgeAsync(NullLogger.Instance);

        // An envelope received and handled at another address, still in the inbox until KeepAfterMessageHandling
        var handled = ObjectMother.Envelope();
        handled.Destination = ElsewhereAddress;
        await runtime.Storage.Inbox.StoreIncomingAsync(handled);
        await runtime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        // The same envelope arrives on the queue again, in one batch with three others
        var duplicate = ObjectMother.Envelope();
        duplicate.Id = handled.Id;
        await queue.SendAsync(duplicate);

        var others = new[] { ObjectMother.Envelope(), ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var other in others) await queue.SendAsync(other);

        (await queue.CountAsync()).ShouldBe(4);

        var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
        await using var listener = new OracleQueueListener(queue, runtime, receiver, queue.DataSource, null);

        var popped = await listener.TryPopDurablyAsync(10, runtime.DurabilitySettings, NullLogger.Instance,
            TestContext.Current.CancellationToken);

        popped.Select(x => x.Id).ShouldBe(others.Select(x => x.Id), ignoreOrder: true);
        (await queue.CountAsync()).ShouldBe(0);

        var inbox = await runtime.Storage.Admin.AllIncomingAsync();
        inbox.Where(x => x.Id == duplicate.Id).ShouldHaveSingleItem().Status.ShouldBe(EnvelopeStatus.Handled);
        inbox.Where(x => others.Any(o => o.Id == x.Id)).ShouldAllBe(x => x.Status == EnvelopeStatus.Incoming);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

public record Bug5411Message;
