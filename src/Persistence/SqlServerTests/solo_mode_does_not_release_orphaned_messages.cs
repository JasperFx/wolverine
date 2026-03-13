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
    private IHost theSoloHost;
    private IHost theBalancedHost;
    private const string SoloSchema = "solo_orphan";
    private const string BalancedSchema = "balanced_orphan";

    public async Task InitializeAsync()
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

    public async Task DisposeAsync()
    {
        await theSoloHost.StopAsync();
        await theBalancedHost.StopAsync();
    }

    [Fact]
    public void solo_mode_build_operation_batch_excludes_orphaned_release()
    {
        var runtime = theSoloHost.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        var agent = new DurabilityAgent(runtime, database);

        var operations = agent.buildOperationBatch();

        // Should NOT contain any orphaned message release operations in Solo mode
        operations.ShouldNotContain(op => op is ReleaseOrphanedMessagesOperation);
        operations.ShouldNotContain(op => op is ReleaseOrphanedMessagesForAncillaryOperation);
    }

    [Fact]
    public void solo_mode_build_operation_batch_excludes_orphaned_release_even_with_active_nodes()
    {
        var runtime = theSoloHost.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        var agent = new DurabilityAgent(runtime, database);

        var operations = agent.buildOperationBatch(activeNodeNumbers: new List<int> { 1, 2, 3 });

        // Should NOT contain any orphaned message release operations in Solo mode
        operations.ShouldNotContain(op => op is ReleaseOrphanedMessagesOperation);
        operations.ShouldNotContain(op => op is ReleaseOrphanedMessagesForAncillaryOperation);
    }

    [Fact]
    public void balanced_mode_build_operation_batch_includes_orphaned_release()
    {
        var runtime = theBalancedHost.GetRuntime();
        var database = (IMessageDatabase)runtime.Storage;

        var agent = new DurabilityAgent(runtime, database);

        var operations = agent.buildOperationBatch();

        // SHOULD contain the orphaned message release operation for main databases in Balanced mode
        operations.ShouldContain(op => op is ReleaseOrphanedMessagesOperation);
    }
}
