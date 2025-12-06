using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RoutingSlip.Tests;

public class end_to_end : IAsyncLifetime
{
    private IHost _pubHost;
    private IHost _firstHost;
    private IHost _secondHost;
    private IHost _thirdHost;

    private readonly int _firstHostPort = PortFinder.GetAvailablePort();
    private readonly int _secondHostPort = PortFinder.GetAvailablePort();
    private readonly int _thirdHostPort = PortFinder.GetAvailablePort();
    
    [Fact]
    public async Task end_to_end_message_using_routing_slip()
    {
        ResetActivityState();

        // Arrange
        var builder = new RoutingSlipBuilder();
        builder.AddActivity("activity1", new Uri($"tcp://localhost:{_firstHostPort}"));
        builder.AddActivity("activity2", new Uri($"tcp://localhost:{_secondHostPort}"));
        builder.AddActivity("activity3", new Uri($"tcp://localhost:{_thirdHostPort}"));
        
        // Act
        var session = await _pubHost.TrackActivity()
            .AlsoTrack(_firstHost)
            .AlsoTrack(_secondHost)
            .AlsoTrack(_thirdHost)
            .ExecuteAndWaitAsync(ctx => ctx.ExecuteRoutingSlip(builder.Build()));
        
        // Assert
        var received = session.Received.MessagesOf<ExecutionContext>().ToList();
        received.Count.ShouldBe(3);

        var trackingNumber = received.Select(x => x.RoutingSlip.TrackingNumber).Distinct().Single();
        var executions = ActivityTracker.GetExecutions(trackingNumber);

        executions.Select(x => x.ActivityName).ShouldBe(new[] { "activity1", "activity2", "activity3" });
        executions.Select(x => x.Destination.Port).ShouldBe(new[] { _firstHostPort, _secondHostPort, _thirdHostPort });
        executions.Count(x => x.ActivityName == "activity2").ShouldBe(1);
        ActivityTracker.GetCompensations(trackingNumber).ShouldBeEmpty();
    }
    
    [Fact]
    public async Task end_to_end_message_using_routing_slip_with_compensation()
    {
        ResetActivityState();

        // Arrange
        var builder = new RoutingSlipBuilder();
        builder.AddActivity("activity1", new Uri($"tcp://localhost:{_firstHostPort}"));
        builder.AddActivity("activity2", new Uri($"tcp://localhost:{_secondHostPort}"));
        builder.AddActivity("errorActivity3", new Uri($"tcp://localhost:{_thirdHostPort}"));
        
        // Act
        var session = await _pubHost.TrackActivity()
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .AlsoTrack(_firstHost)
            .AlsoTrack(_secondHost)
            .AlsoTrack(_thirdHost)
            .ExecuteAndWaitAsync(ctx => ctx.ExecuteRoutingSlip(builder.Build()));
        
        // Assert
        var receivedExecutions = session.Received.MessagesOf<ExecutionContext>().ToList();
        receivedExecutions.Count.ShouldBe(3);
        
        var receivedCompensations = session.Received.MessagesOf<CompensationContext>().ToList();
        receivedCompensations.Count.ShouldBe(2);

        var trackingNumber = receivedExecutions.Select(x => x.RoutingSlip.TrackingNumber).Distinct().Single();
        var compensationEvents = ActivityTracker.GetCompensations(trackingNumber);
        compensationEvents.Select(x => x.ActivityName).OrderBy(x => x).ShouldBe(new[] { "activity1", "activity2" }.OrderBy(x => x).ToArray());
        compensationEvents.Select(x => x.Destination.Port).OrderBy(x => x).ShouldBe(new[] {_secondHostPort, _firstHostPort}.OrderBy(x => x).ToArray());
        ActivityTracker.GetExecutions(trackingNumber).Any(x => x.ActivityName == "errorActivity3").ShouldBeTrue();
    }

    [Fact]
    public async Task retries_failed_activity_before_compensating_when_policy_overridden()
    {
        ResetActivityState();

        var retryPolicy = new Action<RoutingSlipOptions>(options =>
        {
            options.OverridePolicy = policy => policy.RetryTimes(1);
        });

        ActivityHandlerBehavior.FailNextExecution("retryActivity2");

        var firstPort = PortFinder.GetAvailablePort();
        var secondPort = PortFinder.GetAvailablePort();
        var thirdPort = PortFinder.GetAvailablePort();

        var pubHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip(retryPolicy)
                .PublishMessage<ExecutionContext>().ToPort(firstPort);
        }).StartAsync();

        var firstHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip(retryPolicy)
                .ListenAtPort(firstPort);
        }).StartAsync();

        var secondHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip(retryPolicy)
                .ListenAtPort(secondPort);
        }).StartAsync();

        var thirdHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip(retryPolicy)
                .ListenAtPort(thirdPort);
        }).StartAsync();

        var hosts = new[] { pubHost, firstHost, secondHost, thirdHost };

        var builder = new RoutingSlipBuilder();
        builder.AddActivity("activity1", new Uri($"tcp://localhost:{firstPort}"));
        builder.AddActivity("retryActivity2", new Uri($"tcp://localhost:{secondPort}"));
        builder.AddActivity("activity3", new Uri($"tcp://localhost:{thirdPort}"));

        try
        {
            var session = await pubHost.TrackActivity()
                .IncludeExternalTransports()
                .DoNotAssertOnExceptionsDetected()
                .AlsoTrack(firstHost)
                .AlsoTrack(secondHost)
                .AlsoTrack(thirdHost)
                .ExecuteAndWaitAsync(ctx => ctx.ExecuteRoutingSlip(builder.Build()));

            var trackingNumber = session.Received.MessagesOf<ExecutionContext>()
                .Select(x => x.RoutingSlip.TrackingNumber).Distinct().Single();

            var executions = ActivityTracker.GetExecutions(trackingNumber);
            executions.Count(x => x.ActivityName == "retryActivity2").ShouldBe(2);
            executions.Where(x => x.ActivityName == "retryActivity2")
                .Select(x => x.Attempt).ShouldBe([1, 2]);

            ActivityTracker.GetCompensations(trackingNumber).ShouldBeEmpty();
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
    }
    
    #region Test setup

    public async Task InitializeAsync()
    {
        _pubHost = await Host.CreateDefaultBuilder().UseWolverine( opts =>
        {
            opts.UseRoutingSlip()
                .PublishMessage<ExecutionContext>().ToPort(_firstHostPort);
            }).StartAsync();

        _firstHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip()
                .ListenAtPort(_firstHostPort);
        }).StartAsync();
        
        _secondHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip()
                .ListenAtPort(_secondHostPort);
        }).StartAsync();
        
        _thirdHost = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UseRoutingSlip()
                .ListenAtPort(_thirdHostPort);
        }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _pubHost.StopAsync();
        await _firstHost.StopAsync();
        await _secondHost.StopAsync();
        await _thirdHost.StopAsync();
    }

    private static void ResetActivityState()
    {
        ActivityTracker.Reset();
        ActivityHandlerBehavior.Reset();
    }

    #endregion
}

public sealed class ActivityHandler(ILogger<ActivityHandler> logger) : IExecutionActivity, ICompensationActivity
{
    public ValueTask HandleAsync(ExecutionContext context, CancellationToken ct)
    {
        if (context.CurrentActivity is { } activity)
        {
            ActivityTracker.RecordExecution(context.RoutingSlip.TrackingNumber, activity.Name, activity.DestinationUri);
        }

        logger.LogInformation("ExecutionContext on {Host} Tracking={Tracking}",
            Environment.MachineName, context.RoutingSlip.TrackingNumber);

        if (context.CurrentActivity?.Name == "errorActivity3" ||
            ActivityHandlerBehavior.ShouldFail(context.CurrentActivity?.Name))
        {
            throw new Exception("Something went wrong");
        }
        
        return ValueTask.CompletedTask;
    }
    
    public ValueTask HandleAsync(CompensationContext context,  CancellationToken ct)
    {
        ActivityTracker.RecordCompensation(context.RoutingSlip.TrackingNumber,
            context.CurrentLog.ExecutionName, context.CurrentLog.DestinationUri);

        logger.LogInformation("CompensationContext on {Host} racking={Tracking} " +
                              "ExecutionId={ExecutionId} ExecutionId={ExecutionName}",
            Environment.MachineName, context.RoutingSlip.TrackingNumber, 
            context.ExecutionId, context.CurrentLog.ExecutionName);
        return ValueTask.CompletedTask;
    }
}

internal static class ActivityHandlerBehavior
{
    private static readonly ConcurrentDictionary<string, int> Failures =
        new(StringComparer.OrdinalIgnoreCase);

    public static void FailNextExecution(string activityName, int attempts = 1)
    {
        Failures[activityName] = attempts;
    }

    public static bool ShouldFail(string? activityName)
    {
        if (string.IsNullOrEmpty(activityName))
        {
            return false;
        }

        while (true)
        {
            if (!Failures.TryGetValue(activityName, out var remaining) || remaining == 0)
            {
                return false;
            }

            if (Failures.TryUpdate(activityName, remaining - 1, remaining))
            {
                return true;
            }
        }
    }

    public static void Reset() => Failures.Clear();
}
