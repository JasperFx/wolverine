using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.RoutingSlip.Abstractions;
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
    }
    
    [Fact]
    public async Task end_to_end_message_using_routing_slip_with_compensation()
    {
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

    #endregion
}

public sealed class ActivityHandler(ILogger<ActivityHandler> logger) : IExecutionActivity, ICompensationActivity
{
    public ValueTask HandleAsync(ExecutionContext context, CancellationToken ct)
    {
        logger.LogInformation("ExecutionContext on {Host} Tracking={Tracking}",
            Environment.MachineName, context.RoutingSlip.TrackingNumber);

        if (context.CurrentActivity?.Name == "errorActivity3")
        {
            throw new Exception("Something went wrong");
        }
        
        return ValueTask.CompletedTask;
    }
    
    public ValueTask HandleAsync(CompensationContext context,  CancellationToken ct)
    {
        logger.LogInformation("CompensationContext on {Host} racking={Tracking} " +
                              "ExecutionId={ExecutionId} ExecutionId={ExecutionName}",
            Environment.MachineName, context.RoutingSlip.TrackingNumber, 
            context.ExecutionId, context.CurrentLog.ExecutionName);
        return ValueTask.CompletedTask;
    }
}