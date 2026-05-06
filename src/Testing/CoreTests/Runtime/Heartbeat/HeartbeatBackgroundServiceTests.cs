using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Heartbeat;
using Wolverine.Runtime.Routing;
using Xunit;

namespace CoreTests.Runtime.Heartbeat;

public class HeartbeatBackgroundServiceTests
{
    private static IWolverineRuntime BuildRuntime(WolverineOptions options, IMessageRouter router)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.MessageTracking.Returns(Substitute.For<IMessageTracker>());
        runtime.Storage.Returns(Substitute.For<IMessageStore>());
        runtime.Logger.Returns(NullLogger.Instance);
        runtime.RoutingFor(typeof(WolverineHeartbeat)).Returns(router);
        return runtime;
    }

    private static (WolverineOptions options, IMessageRouter router) BuildPublishingOptions(string serviceName)
    {
        var options = new WolverineOptions { ServiceName = serviceName };
        // Empty envelope array so MessageBus.PublishAsync short-circuits and we don't
        // need to wire up a real persistence/sending pipeline.
        var router = Substitute.For<IMessageRouter>();
        router.RouteForPublish(Arg.Any<object>(), Arg.Any<DeliveryOptions?>())
            .Returns(Array.Empty<Envelope>());
        return (options, router);
    }

    [Fact]
    public async Task publishes_repeatedly_at_the_configured_interval()
    {
        var (options, router) = BuildPublishingOptions("HeartbeatService");
        options.EnableHeartbeats(50.Milliseconds());

        var runtime = BuildRuntime(options, router);
        var service = new HeartbeatBackgroundService(runtime);

        using var cts = new CancellationTokenSource();
        var execution = service.StartAsync(cts.Token);

        // Run the service for 250ms with a 50ms interval — expect at least 2 publishes
        await Task.Delay(250);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        var publishCount = router.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMessageRouter.RouteForPublish));

        publishCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task heartbeat_carries_service_name_and_node_number()
    {
        var (options, router) = BuildPublishingOptions("CritterFleet");
        options.EnableHeartbeats(20.Milliseconds());
        options.Durability.AssignedNodeNumber = 4242;

        var runtime = BuildRuntime(options, router);
        var service = new HeartbeatBackgroundService(runtime);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Wait long enough for at least one publish
        await Task.Delay(120);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        var heartbeats = router.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IMessageRouter.RouteForPublish))
            .Select(c => c.GetArguments()[0])
            .OfType<WolverineHeartbeat>()
            .ToList();

        heartbeats.ShouldNotBeEmpty();
        var first = heartbeats[0];
        first.ServiceName.ShouldBe("CritterFleet");
        first.NodeNumber.ShouldBe(4242);
        first.Uptime.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task does_not_publish_when_disabled()
    {
        var (options, router) = BuildPublishingOptions("Disabled");
        options.EnableHeartbeats(20.Milliseconds());
        options.Heartbeat.Enabled = false;

        var runtime = BuildRuntime(options, router);
        var service = new HeartbeatBackgroundService(runtime);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        await Task.Delay(120);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        var publishCount = router.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IMessageRouter.RouteForPublish));

        publishCount.ShouldBe(0);
    }

    [Fact]
    public void enable_heartbeats_extension_sets_policy_and_registers_hosted_service()
    {
        var options = new WolverineOptions();

        options.EnableHeartbeats(7.Seconds());

        options.Heartbeat.Enabled.ShouldBeTrue();
        options.Heartbeat.Interval.ShouldBe(7.Seconds());

        options.Services.ShouldContain(d =>
            d.ServiceType == typeof(HeartbeatBackgroundService));
    }

    [Fact]
    public void enable_heartbeats_without_interval_keeps_default()
    {
        var options = new WolverineOptions();
        var defaultInterval = options.Heartbeat.Interval;

        options.EnableHeartbeats();

        options.Heartbeat.Enabled.ShouldBeTrue();
        options.Heartbeat.Interval.ShouldBe(defaultInterval);
    }
}
