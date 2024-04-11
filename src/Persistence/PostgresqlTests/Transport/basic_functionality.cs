using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PersistenceTests;
using Shouldly;
using TestingSupport;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace PostgresqlTests.Transport;

public class basic_functionality : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public basic_functionality(ITestOutputHelper output)
    {
        _output = output;
    }

    private IHost theHost;
    private PostgresqlTransport theTransport;
    private PostgresqlQueue theQueue;
    private IMessageStore theMessageStore;

    public async Task InitializeAsync()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("transports");
        await conn.CloseAsync();
        
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, "transports");
                opts.ListenToPostgresqlQueue("one");
            }).StartAsync();

        var wolverineRuntime = theHost.GetRuntime();
        theTransport = wolverineRuntime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        theQueue = theTransport.Queues["one"];

        theMessageStore = wolverineRuntime.Storage;
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task expected_tables_exist_for_queue()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var names = await conn.ExistingTablesAsync(schemas: ["transports"]);

        await conn.CloseAsync();
        
        names.Any(x => x.QualifiedName == "transports.wolverine_queue_one").ShouldBeTrue();
        names.Any(x => x.QualifiedName == "transports.wolverine_queue_one_scheduled").ShouldBeTrue();
    }

    [Fact]
    public async Task connect_smoke_test()
    {
        await theTransport.ConnectAsync(theHost.GetRuntime());
    }

    [Fact]
    public async Task purge_queue_smoke_test()
    {
        await theQueue.PurgeAsync(NullLogger.Instance);
    }

    [Fact]
    public async Task check_queue_smoke_test()
    {
        (await theQueue.CheckAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task teardown_and_setup()
    {
        await theQueue.TeardownAsync(NullLogger.Instance);
        await theQueue.SetupAsync(NullLogger.Instance);
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