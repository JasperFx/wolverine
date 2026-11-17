using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

/// <summary>
/// GH-5411. An envelope the inbox already holds -- handled earlier after being received at another address --
/// used to fail the durable pop's inbox INSERT with a duplicate key, so the whole batch rolled back onto the
/// queue on every poll. Now the duplicate leaves the queue and is discarded, and the rest is received.
/// </summary>
[Collection("mysql")]
public class Bug_5411_durable_pop_skips_envelopes_the_inbox_holds
{
    private static readonly Uri ElsewhereAddress = new("mysql://elsewhere/");

    private static async Task<IHost> startAsync(string database, MessageIdentity identity)
    {
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS {database}; CREATE DATABASE {database};";
            await cmd.ExecuteNonQueryAsync();
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageIdentity = identity;
                opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema: database,
                    transportSchema: database);

                // Declared without a listener: the test pops by hand
                opts.PublishMessage<Bug5411Message>().ToMySqlQueue("gh5411");
            }).StartAsync();
    }

    private static async Task<(MySqlQueue queue, MySqlQueueListener listener, Envelope duplicate, Envelope[] others)>
        arrangeAsync(IHost host)
    {
        var runtime = host.GetRuntime();
        var queue = runtime.Options.Transports.GetOrCreate<MySqlTransport>().Queues["gh5411"];

        var handled = ObjectMother.Envelope();
        handled.Destination = ElsewhereAddress;
        await runtime.Storage.Inbox.StoreIncomingAsync(handled);
        await runtime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var duplicate = ObjectMother.Envelope();
        duplicate.Id = handled.Id;
        await queue.SendAsync(duplicate);

        var others = new[] { ObjectMother.Envelope(), ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var other in others) await queue.SendAsync(other);

        (await queue.CountAsync()).ShouldBe(4);

        var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
        var listener = new MySqlQueueListener(queue, runtime, receiver, queue.DataSource, null);

        return (queue, listener, duplicate, others);
    }

    [Fact]
    public async Task a_duplicate_does_not_hold_up_the_rest_of_the_batch()
    {
        using var host = await startAsync("wolverine_gh5411", MessageIdentity.IdOnly);
        var runtime = host.GetRuntime();
        var (queue, listener, duplicate, others) = await arrangeAsync(host);
        await using var _ = listener;

        var popped = await listener.TryPopDurablyAsync(10, runtime.DurabilitySettings, NullLogger.Instance,
            CancellationToken.None);

        popped.Select(x => x.Id).ShouldBe(others.Select(x => x.Id), ignoreOrder: true);
        (await queue.CountAsync()).ShouldBe(0);

        var inbox = await runtime.Storage.Admin.AllIncomingAsync();
        inbox.Where(x => x.Id == duplicate.Id).ShouldHaveSingleItem().Status.ShouldBe(EnvelopeStatus.Handled);
        inbox.Count(x => x.Status == EnvelopeStatus.Incoming).ShouldBe(3);
    }

    [Fact]
    public async Task under_id_and_destination_a_copy_received_elsewhere_is_not_a_duplicate()
    {
        using var host = await startAsync("wolverine_gh5411_by_destination", MessageIdentity.IdAndDestination);
        var runtime = host.GetRuntime();
        var (queue, listener, duplicate, others) = await arrangeAsync(host);
        await using var _ = listener;

        var popped = await listener.TryPopDurablyAsync(10, runtime.DurabilitySettings, NullLogger.Instance,
            CancellationToken.None);

        popped.Select(x => x.Id).ShouldBe(others.Select(x => x.Id).Append(duplicate.Id), ignoreOrder: true);
        (await queue.CountAsync()).ShouldBe(0);
    }
}

public record Bug5411Message;
