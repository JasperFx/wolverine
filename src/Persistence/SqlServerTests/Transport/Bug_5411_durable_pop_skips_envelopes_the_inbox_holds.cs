using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

/// <summary>
/// GH-5411. An envelope the inbox already holds -- handled earlier after being received at another address --
/// used to fail the durable pop's inbox INSERT with a primary key violation, so the whole batch rolled back onto
/// the queue on every poll. Now the duplicate leaves the queue and is discarded, and the rest is received.
/// </summary>
public class Bug_5411_durable_pop_skips_envelopes_the_inbox_holds
{
    private static readonly Uri ElsewhereAddress = new("sqlserver://elsewhere/");

    private static async Task<IHost> startAsync(string schema, MessageIdentity identity)
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(schema);
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageIdentity = identity;
                opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schema).AutoProvision();

                // Declared without a listener: the test pops by hand
                opts.PublishMessage<Bug5411Message>().ToSqlServerQueue("gh5411");
            }).StartAsync();
    }

    private static async Task<(SqlServerQueue queue, SqlServerQueueListener listener, Envelope duplicate, Envelope[] others)>
        arrangeAsync(IHost host)
    {
        var runtime = host.GetRuntime();
        var queue = runtime.Options.Transports.OfType<SqlServerTransport>().Single().Queues["gh5411"];

        var handled = ObjectMother.Envelope();
        handled.Destination = ElsewhereAddress;
        await runtime.Storage.Inbox.StoreIncomingAsync(handled);
        await runtime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var duplicate = ObjectMother.Envelope();
        duplicate.Id = handled.Id;
        await queue.SendAsync(duplicate, CancellationToken.None);

        var others = new[] { ObjectMother.Envelope(), ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var other in others) await queue.SendAsync(other, CancellationToken.None);

        (await queue.CountAsync()).ShouldBe(4);

        var receiver = new DurableReceiver(queue, runtime, Substitute.For<IHandlerPipeline>());
        var listener = new SqlServerQueueListener(queue, runtime, receiver, Servers.SqlServerConnectionString, null);

        return (queue, listener, duplicate, others);
    }

    [Fact]
    public async Task a_duplicate_does_not_hold_up_the_rest_of_the_batch()
    {
        using var host = await startAsync("gh5411", MessageIdentity.IdOnly);
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
        using var host = await startAsync("gh5411_by_destination", MessageIdentity.IdAndDestination);
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
