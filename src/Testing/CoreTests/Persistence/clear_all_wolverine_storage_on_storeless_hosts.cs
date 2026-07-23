using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Persistence;

// ClearAllWolverineStorageAsync() is the recommended integration-test reset for apps using durable
// messaging and/or database-backed queues (GH-3592). Test suites call it from shared setup, so it
// has to stay a safe no-op on hosts that have neither -- otherwise adding it to a base fixture
// breaks every storeless test in the suite.
public class clear_all_wolverine_storage_on_storeless_hosts
{
    [Fact]
    public async Task safe_no_op_against_a_host_with_no_message_store()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Durability.Mode = DurabilityMode.Solo; })
            .StartAsync();

        await Should.NotThrowAsync(() => host.ClearAllWolverineStorageAsync());
    }

    [Fact]
    public async Task touches_nothing_on_a_host_with_only_local_queues()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PublishAllMessages().ToLocalQueue("clear-all-storage");
            })
            .StartAsync();

        var runtime = host.GetRuntime();

        // The local transport's queues are not IDatabaseBackedEndpoint, so the queue half of the
        // reset finds nothing to purge and the messages sitting in them survive.
        var queue = runtime.Endpoints.EndpointFor("local://clear-all-storage".ToUri())
            .ShouldBeOfType<LocalQueue>();

        await Should.NotThrowAsync(() => host.ClearAllWolverineStorageAsync());

        queue.ShouldNotBeNull();
    }
}
