using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace CoreTests.ErrorHandling.Faults.Integration;

public class FaultBypassTracingTests
{
    public record OrderPlaced(string OrderId);

    private static IWolverineRuntime BuildRuntime(FaultPublishingMode globalMode)
    {
        var options = new WolverineOptions();
        if (globalMode != FaultPublishingMode.None)
        {
            options.PublishFaultEvents(includeDiscarded: globalMode == FaultPublishingMode.DlqAndDiscard);
        }

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.Logger.Returns(NullLogger.Instance);
        return runtime;
    }

    private static (Activity activity, ActivityListener listener) StartActivity()
    {
        var source = new ActivitySource("FaultBypassTracingTests");
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "FaultBypassTracingTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var activity = source.StartActivity("test")!;
        return (activity, listener);
    }

    [Fact]
    public async Task unknown_type_dlq_emits_bypass_event_when_fault_publishing_is_globally_enabled()
    {
        var runtime = BuildRuntime(FaultPublishingMode.DlqOnly);
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var envelope = new Envelope { Id = Guid.NewGuid(), MessageType = "Some.Unknown.Type" };
        lifecycle.Envelope.Returns(envelope);

        var (activity, listener) = StartActivity();
        try
        {
            var handler = new MoveUnknownMessageToDeadLetterQueue();
            await handler.HandleAsync(lifecycle, runtime);

            activity.Stop();
            activity.Events.Any(e => e.Name == WolverineTracing.FaultBypassedUnknownType).ShouldBeTrue();
            var bypassEvent = activity.Events.First(e => e.Name == WolverineTracing.FaultBypassedUnknownType);
            bypassEvent.Tags.ShouldContain(t =>
                t.Key == WolverineTracing.MessageType && (string?)t.Value == "Some.Unknown.Type");
        }
        finally { activity.Dispose(); listener.Dispose(); }
    }

    [Fact]
    public async Task unknown_type_dlq_skips_bypass_event_when_fault_publishing_is_disabled()
    {
        var runtime = BuildRuntime(FaultPublishingMode.None);
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        var envelope = new Envelope { Id = Guid.NewGuid(), MessageType = "Some.Unknown.Type" };
        lifecycle.Envelope.Returns(envelope);

        var (activity, listener) = StartActivity();
        try
        {
            var handler = new MoveUnknownMessageToDeadLetterQueue();
            await handler.HandleAsync(lifecycle, runtime);

            activity.Stop();
            activity.Events
                .Where(e => e.Name == WolverineTracing.FaultBypassedUnknownType)
                .ShouldBeEmpty();
        }
        finally { activity.Dispose(); listener.Dispose(); }
    }

    [Fact]
    public async Task send_side_dlq_emits_bypass_event_when_fault_publishing_is_globally_enabled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.PublishFaultEvents())
            .StartAsync();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(OrderPlaced).ToMessageTypeName(),
            Message = new OrderPlaced("o-1"),
        };

        var agent = Substitute.For<ISendingAgent>();
        var lifecycle = new SendingEnvelopeLifecycle(envelope, runtime, agent, outbox: null);

        var (activity, listener) = StartActivity();
        try
        {
            await lifecycle.MoveToDeadLetterQueueAsync(new InvalidOperationException("send failed"));

            activity.Stop();
            activity.Events.Any(e => e.Name == WolverineTracing.FaultBypassedSendSide).ShouldBeTrue();
            var bypassEvent = activity.Events.First(e => e.Name == WolverineTracing.FaultBypassedSendSide);
            bypassEvent.Tags.ShouldContain(t =>
                t.Key == WolverineTracing.MessageType
                && (string?)t.Value == typeof(OrderPlaced).ToMessageTypeName());
        }
        finally { activity.Dispose(); listener.Dispose(); await host.StopAsync(); }
    }

    [Fact]
    public async Task send_side_dlq_skips_bypass_event_when_fault_publishing_is_disabled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(_ => { /* no PublishFaultEvents */ })
            .StartAsync();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(OrderPlaced).ToMessageTypeName(),
            Message = new OrderPlaced("o-2"),
        };

        var agent = Substitute.For<ISendingAgent>();
        var lifecycle = new SendingEnvelopeLifecycle(envelope, runtime, agent, outbox: null);

        var (activity, listener) = StartActivity();
        try
        {
            await lifecycle.MoveToDeadLetterQueueAsync(new InvalidOperationException("send failed"));

            activity.Stop();
            activity.Events
                .Where(e => e.Name == WolverineTracing.FaultBypassedSendSide)
                .ShouldBeEmpty();
        }
        finally { activity.Dispose(); listener.Dispose(); await host.StopAsync(); }
    }
}
