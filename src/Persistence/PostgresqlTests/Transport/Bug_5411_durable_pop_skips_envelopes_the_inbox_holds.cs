using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5411. The durable pop moves a batch from the queue table into the inbox in one statement. An envelope the
/// inbox already holds -- handled earlier after being received at another address -- used to fail that statement
/// with a unique violation, so the whole batch rolled back onto the queue on every poll until the old row was
/// deleted. Now the duplicate leaves the queue and is discarded, and the rest of the batch is received.
/// </summary>
[Collection("Postgresql")]
public class Bug_5411_durable_pop_skips_envelopes_the_inbox_holds
{
    private static readonly Uri ElsewhereAddress = new("postgresql://elsewhere/");

    private static async Task<IHost> startAsync(string schema, MessageIdentity identity, bool partitioned = false)
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(schema);
        }

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageIdentity = identity;
                opts.Durability.EnableInboxPartitioning = partitioned;
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                    transportSchema: schema);

                // Declared without a listener: the test pops by hand
                opts.PublishMessage<Bug5411Message>().ToPostgresqlQueue("gh5411");
            }).StartAsync();
    }

    private static async Task<(PostgresqlQueue queue, PostgresqlQueueListener listener, Envelope duplicate, Envelope[] others)>
        arrangeAsync(IHost host)
    {
        var runtime = host.GetRuntime();
        var queue = runtime.Options.Transports.GetOrCreate<PostgresqlTransport>().Queues["gh5411"];

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
        var listener = new PostgresqlQueueListener(queue, runtime, receiver, queue.DataSource, null);

        return (queue, listener, duplicate, others);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_duplicate_does_not_hold_up_the_rest_of_the_batch(bool partitioned)
    {
        using var host = await startAsync(partitioned ? "gh5411_partitioned" : "gh5411", MessageIdentity.IdOnly, partitioned);
        var runtime = host.GetRuntime();
        var (queue, listener, duplicate, others) = await arrangeAsync(host);
        await using var _ = listener;

        var popped = await listener.TryPopDurablyAsync(10, runtime.DurabilitySettings, NullLogger.Instance,
            CancellationToken.None);

        popped.Select(x => x.Id).ShouldBe(others.Select(x => x.Id), ignoreOrder: true);

        // The duplicate left the queue too, so the next poll does not trip over it again
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
