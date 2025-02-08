using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

public class data_operations : IAsyncLifetime
{
    public static int count = 0;

    private IHost _host;
    private SqlServerTransport theTransport;
    private IStatefulResource? theResource;
    private SqlServerQueue theQueue;
    private IMessageStore theMessageStore;

    public async Task InitializeAsync()
    {
        var schemaName = "sqlserver" + ++count;

        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(schemaName);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schemaName)

                    // Helpful for quicker dev
                    .AutoProvision()

                    // This would only be applicable in testing
                    .AutoPurgeOnStartup();

                // Whatever you're doing to local queues today probably changes to this:
                opts.ListenToSqlServerQueue("one");
                opts.PublishMessage<Message1>().ToSqlServerQueue("one");

                // I added this to cover the inbox/outbox
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var runtime = _host.GetRuntime();

        theMessageStore = runtime.Storage;

        theTransport = runtime.Options.Transports.OfType<SqlServerTransport>().Single();
        await theTransport.InitializeAsync(runtime);

        theTransport.TryBuildStatefulResource(runtime, out theResource).ShouldBeTrue();

        await theResource.Setup(CancellationToken.None);
        await theResource.ClearState(CancellationToken.None);

        theQueue = theTransport.Queues["one"];
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task send_not_scheduled_smoke_test()
    {
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task send_not_scheduled_is_idempotent_smoke_test()
    {
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope, CancellationToken.None);
        await theQueue.SendAsync(envelope, CancellationToken.None);
        await theQueue.SendAsync(envelope, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task send_scheduled_smoke_test()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduleDelay = 1.Hours();
        envelope.IsScheduledForLater(DateTimeOffset.UtcNow).ShouldBeTrue();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task does_fine_with_double_schedule()
    {
        var envelope = ObjectMother.Envelope();
        envelope.ScheduleDelay = 1.Hours();
        envelope.IsScheduledForLater(DateTimeOffset.UtcNow).ShouldBeTrue();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope, CancellationToken.None);

        // Does not blow up
        await theQueue.SendAsync(envelope, CancellationToken.None);
        await theQueue.SendAsync(envelope, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task delete_expired_smoke_test()
    {
        var databaseTime = await theTransport.SystemTimeAsync();

        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = databaseTime.Subtract(1.Hours());
        await theQueue.SendAsync(envelope, CancellationToken.None);

        var envelope2 = ObjectMother.Envelope();
        envelope2.DeliverBy = databaseTime.Add(1.Hours());
        await theQueue.SendAsync(envelope2, CancellationToken.None);

        var envelope3 = ObjectMother.Envelope();
        envelope3.DeliverBy = null;
        await theQueue.SendAsync(envelope3, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(3);
        await theQueue.DeleteExpiredAsync(CancellationToken.None);
        (await theQueue.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task move_from_outgoing_to_queue_async()
    {
        (await theQueue.CountAsync()).ShouldBe(0);

        var envelope = ObjectMother.Envelope();
        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 0);

        await theQueue.MoveFromOutgoingToQueueAsync(envelope, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(1);

        var stats = await theMessageStore.Admin.FetchCountsAsync();
        stats.Outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task move_from_outgoing_to_scheduled_async()
    {
        (await theQueue.CountAsync()).ShouldBe(0);

        var envelope = ObjectMother.Envelope();
        envelope.ScheduleDelay = 1.Hours();
        envelope.IsScheduledForLater(DateTimeOffset.UtcNow).ShouldBeTrue();
        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 0);

        await theQueue.MoveFromOutgoingToScheduledAsync(envelope, CancellationToken.None);

        (await theQueue.ScheduledCountAsync()).ShouldBe(1);

        var stats = await theMessageStore.Admin.FetchCountsAsync();
        stats.Outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task move_from_scheduled_to_queue()
    {
        var systemTime = await theTransport.SystemTimeAsync();

        // Should be moved
        for (int i = 0; i < 20; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.ScheduledTime = systemTime.Subtract(1.Days());
            await theQueue.ScheduleMessageAsync(envelope, CancellationToken.None);
        }

        // Push the dates back

        for (int i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.ScheduledTime = systemTime.Add(1.Days());
            await theQueue.ScheduleMessageAsync(envelope, CancellationToken.None);
        }

        (await theQueue.ScheduledCountAsync()).ShouldBe(30);
        (await theQueue.CountAsync()).ShouldBe(0);

        var scheduledCount = await theQueue.MoveScheduledToReadyQueueAsync(CancellationToken.None);
        scheduledCount.ShouldBe(20);

        (await theQueue.ScheduledCountAsync()).ShouldBe(10);
        (await theQueue.CountAsync()).ShouldBe(20);
    }

    [Fact]
    public async Task pop_off_buffered()
    {
        for (int i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            await theQueue.SendAsync(envelope, CancellationToken.None);
        }

        var popped = await theQueue.TryPopAsync(5, NullLogger.Instance, CancellationToken.None);
        popped.Count.ShouldBe(5);

        (await theQueue.CountAsync()).ShouldBe(5);
    }

    [Fact]
    public async Task move_to_incoming()
    {
        for (int i = 0; i < 20; i++)
        {
            var envelope = ObjectMother.Envelope();
            await theQueue.SendAsync(envelope, CancellationToken.None);
        }

        var popped = await theQueue.TryPopDurablyAsync(5, new DurabilitySettings{AssignedNodeNumber = 21}, NullLogger.Instance, CancellationToken.None);
        popped.Count.ShouldBe(5);

        (await theQueue.CountAsync()).ShouldBe(15);

        var incoming = await theMessageStore.Admin.AllIncomingAsync();
        incoming.Count.ShouldBe(5);
        incoming.All(x => x.OwnerId == 21).ShouldBeTrue();
    }
}