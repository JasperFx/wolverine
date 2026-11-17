using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace SqliteTests.Transport;

/// <summary>
/// GH-5411. The durable pop already dropped a queue row whose envelope the inbox held for this queue's own
/// address. Under the default <see cref="MessageIdentity.IdOnly"/>, an envelope handled after being received at
/// ANOTHER address is a duplicate too: the inbox key is the id alone, so inserting it failed with a unique
/// violation and rolled the whole batch back onto the queue on every poll.
/// </summary>
[Collection("durable_inbox_is_real_sqlite")]
public class Bug_5411_durable_pop_skips_envelopes_the_inbox_holds
{
    private const string QueueName = "gh5411";
    private static readonly Uri ElsewhereAddress = new("sqlite://elsewhere");

    private static Task<IHost> startAsync(string connectionString, MessageIdentity identity)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = identity;
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseSqlitePersistenceAndTransport(connectionString).AutoProvision();

                // Declared without a listener: the test pops by hand
                opts.PublishAllMessages().ToSqliteQueue(QueueName);
            }).StartAsync();
    }

    private static async Task<(SqliteQueue queue, Envelope duplicate, Envelope[] others)> arrangeAsync(IHost host)
    {
        var store = host.Services.GetRequiredService<IMessageStore>();
        var queue = host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];

        var handled = ObjectMother.Envelope();
        handled.Destination = ElsewhereAddress;
        await store.Inbox.StoreIncomingAsync(handled);
        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var duplicate = ObjectMother.Envelope();
        duplicate.Id = handled.Id;
        await queue.SendAsync(duplicate);

        var others = new[] { ObjectMother.Envelope(), ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var other in others) await queue.SendAsync(other);

        (await queue.CountAsync()).ShouldBe(4);

        return (queue, duplicate, others);
    }

    [Fact]
    public async Task a_duplicate_does_not_hold_up_the_rest_of_the_batch()
    {
        using var database = Servers.CreateDatabase(nameof(Bug_5411_durable_pop_skips_envelopes_the_inbox_holds));
        using var host = await startAsync(database.ConnectionString, MessageIdentity.IdOnly);
        var (queue, duplicate, others) = await arrangeAsync(host);

        await using var listener = new SqliteQueueListener(queue, host.GetRuntime(), Substitute.For<IReceiver>(),
            queue.DataSource, null);

        var popped = await listener.TryPopDurablyAsync(TestContext.Current.CancellationToken);

        popped.Select(x => x.Id).ShouldBe(others.Select(x => x.Id), ignoreOrder: true);
        (await queue.CountAsync()).ShouldBe(0);

        var inbox = await host.Services.GetRequiredService<IMessageStore>().Admin.AllIncomingAsync();
        inbox.Where(x => x.Id == duplicate.Id).ShouldHaveSingleItem().Status.ShouldBe(EnvelopeStatus.Handled);
        inbox.Count(x => x.Status == EnvelopeStatus.Incoming).ShouldBe(3);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task under_id_and_destination_a_copy_received_elsewhere_is_not_a_duplicate()
    {
        using var database = Servers.CreateDatabase(nameof(Bug_5411_durable_pop_skips_envelopes_the_inbox_holds));
        using var host = await startAsync(database.ConnectionString, MessageIdentity.IdAndDestination);
        var (queue, duplicate, others) = await arrangeAsync(host);

        await using var listener = new SqliteQueueListener(queue, host.GetRuntime(), Substitute.For<IReceiver>(),
            queue.DataSource, null);

        var popped = await listener.TryPopDurablyAsync(TestContext.Current.CancellationToken);

        popped.Select(x => x.Id).ShouldBe(others.Select(x => x.Id).Append(duplicate.Id), ignoreOrder: true);
        (await queue.CountAsync()).ShouldBe(0);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}
