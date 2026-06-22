using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Heartbeat;

/// <summary>
/// #3188 — a Solo host with no durable message store (NullMessageStore) never runs the
/// NodeAgentController path, so before the fix it kept a random per-process AssignedNodeNumber
/// and never emitted NodeStarted()/NodeStopped(). The stable identity is now assigned by the
/// runtime before transports start, and SoloHeartbeatService owns the lifecycle bookends.
/// </summary>
public class solo_storeless_node_identity
{
    private static IHost BuildHost(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(configure)
            .Build();
    }

    [Fact]
    public async Task storeless_solo_gets_node_1_and_fires_lifecycle_bookends()
    {
        using var host = BuildHost(opts => opts.Durability.Mode = DurabilityMode.Solo);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var observer = Substitute.For<IWolverineObserver>();
        runtime.Observer = observer;

        await host.StartAsync();

        // Stable identity, set before the messaging transports start.
        runtime.Options.Durability.AssignedNodeNumber.ShouldBe(1);

        // NodeStarted fires once, while transports are up; NodeStopped not yet.
        await observer.Received(1).NodeStarted();
        await observer.DidNotReceive().NodeStopped();

        await host.StopAsync();

        await observer.Received(1).NodeStopped();
    }

    [Fact]
    public async Task storeless_solo_node_number_is_stable_across_restart()
    {
        async Task<int> bootAndReadNodeNumber()
        {
            using var host = BuildHost(opts => opts.Durability.Mode = DurabilityMode.Solo);
            await host.StartAsync();
            var number = host.Services.GetRequiredService<IWolverineRuntime>()
                .Options.Durability.AssignedNodeNumber;
            await host.StopAsync();
            return number;
        }

        var first = await bootAndReadNodeNumber();
        var second = await bootAndReadNodeNumber();

        first.ShouldBe(1);
        second.ShouldBe(1);
    }

    [Fact]
    public async Task does_not_fire_for_a_storeless_non_solo_host()
    {
        // Default mode is Balanced; a storeless Balanced host has no NodeAgentController and must
        // NOT get the Solo lifecycle bookends.
        using var host = BuildHost(_ => { });

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var observer = Substitute.For<IWolverineObserver>();
        runtime.Observer = observer;

        await host.StartAsync();
        await host.StopAsync();

        await observer.DidNotReceive().NodeStarted();
        await observer.DidNotReceive().NodeStopped();
    }
}
