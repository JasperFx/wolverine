using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using NSubstitute;
using Shouldly;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace OracleTests.Transport;

[Collection("oracle")]
public class basic_functionality : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public basic_functionality(ITestOutputHelper output)
    {
        _output = output;
    }

    private IHost theHost = null!;
    private OracleTransport theTransport = null!;
    private OracleQueue theQueue = null!;
    private IMessageStore theMessageStore = null!;
    private WolverineRuntime theRuntime = null!;

    public async Task InitializeAsync()
    {
        // Clean the schema
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        await using var conn = await dataSource.OpenConnectionAsync();
        try
        {
            // Drop queue tables if they exist
            try
            {
                var cmd = conn.CreateCommand("DROP TABLE WOLVERINE.WOLVERINE_QUEUE_ONE CASCADE CONSTRAINTS");
                await cmd.ExecuteNonQueryAsync();
            }
            catch (OracleException) { /* table doesn't exist */ }

            try
            {
                var cmd = conn.CreateCommand("DROP TABLE WOLVERINE.WOLVERINE_QUEUE_ONE_SCHEDULED CASCADE CONSTRAINTS");
                await cmd.ExecuteNonQueryAsync();
            }
            catch (OracleException) { /* table doesn't exist */ }
        }
        finally
        {
            await conn.CloseAsync();
        }

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE")
                    .EnableMessageTransport();
                opts.ListenToOracleQueue("one");
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        theTransport = theHost.GetRuntime().Options.Transports.GetOrCreate<OracleTransport>();
        theQueue = theTransport.Queues["one"];

        theMessageStore = theHost.GetRuntime().Storage;

        theRuntime = theHost.GetRuntime();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
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
        await theQueue.PurgeAsync(NullLogger.Instance);

        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(envelope);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task send_not_scheduled_is_idempotent_smoke_test()
    {
        await theQueue.PurgeAsync(NullLogger.Instance);

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
        await theQueue.PurgeAsync(NullLogger.Instance);

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
        await theQueue.PurgeAsync(NullLogger.Instance);

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
    public async Task move_from_outgoing_to_queue_async()
    {
        await theQueue.PurgeAsync(NullLogger.Instance);
        (await theQueue.CountAsync()).ShouldBe(0);

        var envelope = ObjectMother.Envelope();
        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 0);

        await new OracleQueueSender(theQueue).MoveFromOutgoingToQueueAsync(envelope, CancellationToken.None);

        (await theQueue.CountAsync()).ShouldBe(1);

        var stats = await theMessageStore.Admin.FetchCountsAsync();
        stats.Outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task move_from_outgoing_to_scheduled_async()
    {
        await theQueue.PurgeAsync(NullLogger.Instance);
        (await theQueue.CountAsync()).ShouldBe(0);

        var envelope = ObjectMother.Envelope();
        envelope.ScheduleDelay = 1.Hours();
        envelope.IsScheduledForLater(DateTimeOffset.UtcNow).ShouldBeTrue();
        await theMessageStore.Outbox.StoreOutgoingAsync(envelope, 0);

        await new OracleQueueSender(theQueue).MoveFromOutgoingToScheduledAsync(envelope, CancellationToken.None);

        (await theQueue.ScheduledCountAsync()).ShouldBe(1);

        var stats = await theMessageStore.Admin.FetchCountsAsync();
        stats.Outgoing.ShouldBe(0);
    }
}
