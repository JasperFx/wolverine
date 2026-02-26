using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace SqliteTests.Transport;

public class basic_functionality : SqliteContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _connectionString;
    private readonly SqliteTestDatabase _database;

    public basic_functionality(ITestOutputHelper output)
    {
        _output = output;
        _database = Servers.CreateDatabase(nameof(basic_functionality));
        _connectionString = _database.ConnectionString;
    }

    private IHost theHost;
    private SqliteTransport theTransport;
    private SqliteQueue theQueue;
    private IMessageStore theMessageStore;
    private WolverineRuntime theRuntime;

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlitePersistenceAndTransport(_connectionString);
                opts.Durability.ScheduledJobFirstExecution = 5.Minutes();
                opts.Durability.ScheduledJobPollingTime = 5.Minutes();
                opts.ListenToSqliteQueue("one");
            }).StartAsync();

        theTransport = theHost.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>();
        theQueue = theTransport.Queues["one"];

        theMessageStore = theHost.GetRuntime().Storage;

        theRuntime = theHost.GetRuntime();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task expected_tables_exist_for_queue()
    {
        using var dataSource = new SqliteDataSource(_connectionString);
        await using var conn = (SqliteConnection)await dataSource.OpenConnectionAsync();

        var names = await conn.ExistingTablesAsync();

        names.Any(x => x.Name == "wolverine_queue_one").ShouldBeTrue();
        names.Any(x => x.Name == "wolverine_queue_one_scheduled").ShouldBeTrue();
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
        await theQueue.SendAsync(envelope);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task send_not_scheduled_is_idempotent_smoke_test()
    {
        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope);
        await theQueue.SendAsync(envelope);
        await theQueue.SendAsync(envelope);

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
        await theQueue.SendAsync(envelope);

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
        await theQueue.SendAsync(envelope);

        // Does not blow up
        await theQueue.SendAsync(envelope);
        await theQueue.SendAsync(envelope);

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task delete_expired_smoke_test()
    {
        var databaseTime = await theTransport.SystemTimeAsync();

        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = databaseTime.Subtract(1.Hours());
        await theQueue.SendAsync(envelope);

        var envelope2 = ObjectMother.Envelope();
        envelope2.DeliverBy = databaseTime.Add(1.Hours());
        await theQueue.SendAsync(envelope2);

        var envelope3 = ObjectMother.Envelope();
        envelope3.DeliverBy = null;
        await theQueue.SendAsync(envelope3);

        (await theQueue.CountAsync()).ShouldBe(3);

        var durableReceiver = new DurableReceiver(theQueue, theRuntime, Substitute.For<IHandlerPipeline>());
        await using var theListener = new SqliteQueueListener(theQueue, theRuntime, durableReceiver, theQueue.DataSource, null);

        await theListener.DeleteExpiredAsync(CancellationToken.None);
        (await theQueue.CountAsync()).ShouldBe(2);
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
            await new SqliteQueueSender(theQueue).ScheduleMessageAsync(envelope, CancellationToken.None);
        }

        // Push the dates back

        for (int i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.ScheduledTime = systemTime.Add(1.Days());
            await new SqliteQueueSender(theQueue).ScheduleMessageAsync(envelope, CancellationToken.None);
        }

        (await theQueue.ScheduledCountAsync()).ShouldBe(30);
        (await theQueue.CountAsync()).ShouldBe(0);

        var durableReceiver = new DurableReceiver(theQueue, theRuntime, Substitute.For<IHandlerPipeline>());
        await using var theListener = new SqliteQueueListener(theQueue, theRuntime, durableReceiver, theQueue.DataSource, null);

        var scheduledCount = await theListener.MoveScheduledToReadyQueueAsync(CancellationToken.None);
        scheduledCount.ShouldBe(20);

        (await theQueue.ScheduledCountAsync()).ShouldBe(10);
        (await theQueue.CountAsync()).ShouldBe(20);
    }

}
