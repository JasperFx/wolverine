using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace SqlServerTests;

public class solo_mode_does_not_release_orphaned_messages : IAsyncLifetime
{
    private IHost theSoloHost = null!;
    private IHost theBalancedHost = null!;
    private const string SoloSchema = "solo_orphan";
    private const string BalancedSchema = "balanced_orphan";

    public async ValueTask InitializeAsync()
    {
        theSoloHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SoloSchema);
                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        await theSoloHost.RebuildAllEnvelopeStorageAsync();

        theBalancedHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, BalancedSchema);
                opts.Durability.Mode = DurabilityMode.Balanced;
            }).StartAsync();

        await theBalancedHost.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theSoloHost.StopAsync();
        await theBalancedHost.StopAsync();
        theSoloHost.Dispose();
        theBalancedHost.Dispose();
    }

    [Fact]
    public async Task solo_mode_builds_no_orphan_sweep()
    {
        var runtime = theSoloHost.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        var agent = new DurabilityAgent(runtime, database);

        // A Solo node is the whole cluster: there are no peers whose departure could orphan anything, and
        // releasing on its own restart is exactly what GH-3287 established must not happen.
        (await agent.buildOrphanSweepAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task balanced_mode_builds_the_orphan_sweep()
    {
        var runtime = theBalancedHost.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        var agent = new DurabilityAgent(runtime, database);

        (await agent.buildOrphanSweepAsync()).ShouldBeOfType<ReleaseOrphanedMessagesCommand>();
    }

    [Fact]
    public void the_orphan_sweep_is_no_longer_part_of_the_shared_recovery_batch()
    {
        var runtime = theBalancedHost.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        var agent = new DurabilityAgent(runtime, database);

        // GH-3971: it runs on its own timer in its own transaction now, for the same reason #3116 moved
        // the expired-handled cleanup out -- an unbounded UPDATE must not hold the recovery transaction.
        agent.buildOperationBatch()
            .ShouldNotContain(op => op.Description.Contains("orphan", StringComparison.OrdinalIgnoreCase));
    }
}
