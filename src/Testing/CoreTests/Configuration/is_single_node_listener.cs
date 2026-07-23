using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3590. The durability agents ask this question on every recovery pass to decide whether a destination's
/// inbox rows are theirs to claim, so it has to be right for every <see cref="ListenerScope"/> — and safe for
/// addresses the node has never heard of.
/// </summary>
public class is_single_node_listener
{
    // Deliberately built inside each test rather than from IAsyncLifetime.InitializeAsync: a Wolverine host
    // created from xUnit's async-lifetime path resolves its calling assembly to "testhost", which pins the
    // process-wide RememberedApplicationAssembly and trips remembered_application_assembly_reuse_warning.
    private static async Task withHostAsync(Action<IHost> assertion)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(PortFinder.GetAvailablePort()).ListenWithStrictOrdering("exclusive-one");
                opts.ListenAtPort(PortFinder.GetAvailablePort()).ListenOnlyAtLeader().Named("leader-one");
                opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("competing-one");

                opts.Durability.Mode = DurabilityMode.Solo;
            }).StartAsync();

        assertion(host);
    }

    private static bool isSingleNodeListener(IHost host, string endpointName)
    {
        var endpoints = host.GetRuntime().Endpoints;
        return endpoints.IsSingleNodeListener(endpoints.EndpointByName(endpointName)!.Uri);
    }

    [Fact]
    public Task exclusive_listener_is_a_single_node_listener()
    {
        return withHostAsync(host => isSingleNodeListener(host, "exclusive-one").ShouldBeTrue());
    }

    [Fact]
    public Task leader_pinned_listener_is_a_single_node_listener()
    {
        return withHostAsync(host => isSingleNodeListener(host, "leader-one").ShouldBeTrue());
    }

    [Fact]
    public Task competing_consumers_listener_is_not_a_single_node_listener()
    {
        return withHostAsync(host => isSingleNodeListener(host, "competing-one").ShouldBeFalse());
    }

    [Fact]
    public Task unknown_address_is_not_a_single_node_listener()
    {
        return withHostAsync(host =>
            host.GetRuntime().Endpoints.IsSingleNodeListener(new Uri("tcp://localhost:65001")).ShouldBeFalse());
    }
}
