using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;

namespace SqliteTests.Transport;

public class reset_clears_transport_queue_tables : SqliteContext, IAsyncLifetime
{
    private readonly SqliteTestDatabase _database;
    private IHost theHost = null!;
    private SqliteQueue theQueue = null!;
    private IMessageStore theMessageStore = null!;

    public reset_clears_transport_queue_tables()
    {
        _database = Servers.CreateDatabase(nameof(reset_clears_transport_queue_tables));
    }

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlitePersistenceAndTransport(_database.ConnectionString);

                // No listener is attached to the queue, so nothing drains it out from
                // under the test before the reset runs.
                opts.PublishAllMessages().ToSqliteQueue("resetone");
            }).StartAsync();

        var transport = theHost.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>();
        theQueue = transport.Queues["resetone"];
        theMessageStore = theHost.GetRuntime().Storage;
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task clear_all_async_empties_the_transport_queue_tables()
    {
        // Row in the queue table
        var immediate = ObjectMother.Envelope();
        immediate.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(immediate);

        // Row in the scheduled-message table
        var scheduled = ObjectMother.Envelope();
        scheduled.ScheduleDelay = 1.Hours();
        scheduled.DeliverBy = DateTimeOffset.UtcNow.AddHours(1);
        await theQueue.SendAsync(scheduled);

        (await theQueue.CountAsync()).ShouldBe(1);
        (await theQueue.ScheduledCountAsync()).ShouldBe(1);

        // The message-store reset must also clear the registered transport queue tables,
        // otherwise integration tests over the SQLite queue transport carry rows between runs.
        await theMessageStore.Admin.ClearAllAsync();

        (await theQueue.CountAsync()).ShouldBe(0);
        (await theQueue.ScheduledCountAsync()).ShouldBe(0);
    }
}
