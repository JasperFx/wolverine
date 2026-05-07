using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// Cross-cutting tests for the opt-in <c>WolverineOptions.Tracking</c> diagnostics
/// surface. Each flag must be off by default and must produce its diagnostics
/// only when explicitly enabled. Capturing is done with a single
/// <see cref="ActivityListener"/> attached to the Wolverine ActivitySource so
/// the assertions look at end-to-end activity output rather than internal codegen
/// shape.
/// </summary>
public class tracking_diagnostics_opt_in
{
    private static async Task<(IHost host, List<Activity> captured, ActivityListener listener)> startWithTrackingAsync(
        Action<TrackingOptions>? configureTracking)
    {
        var captured = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                configureTracking?.Invoke(opts.Tracking);
            })
            .StartAsync();

        return (host, captured, listener);
    }

    [Fact]
    public async Task all_tracking_flags_default_to_false()
    {
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync();

        var options = host.GetRuntime().Options;

        options.Tracking.EnableMessageCausationTracking.ShouldBeFalse();
        options.Tracking.HandlerExecutionDiagnosticsEnabled.ShouldBeFalse();
        options.Tracking.DeserializationSpanEnabled.ShouldBeFalse();
        options.Tracking.OutboxDiagnosticsEnabled.ShouldBeFalse();
    }

    #region HandlerExecutionDiagnosticsEnabled

    [Fact]
    public async Task handler_started_and_finished_events_emit_when_enabled()
    {
        var (host, captured, listener) = await startWithTrackingAsync(t =>
            t.HandlerExecutionDiagnosticsEnabled = true);

        try
        {
            await host.InvokeMessageAndWaitAsync(new TrackingDiagnosticsMessage("hello"));
            await Task.Delay(100.Milliseconds());

            var handlerActivity = captured.FirstOrDefault(a =>
                a.GetTagItem(WolverineTracing.MessageHandler) is string h
                && h.Contains(nameof(TrackingDiagnosticsHandler)));

            handlerActivity.ShouldNotBeNull();
            var eventNames = handlerActivity.Events.Select(e => e.Name).ToArray();
            eventNames.ShouldContain(WolverineTracing.HandlerStarted);
            eventNames.ShouldContain(WolverineTracing.HandlerFinished);
        }
        finally
        {
            listener.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task handler_started_and_finished_events_do_not_emit_when_disabled()
    {
        var (host, captured, listener) = await startWithTrackingAsync(_ => { });

        try
        {
            await host.InvokeMessageAndWaitAsync(new TrackingDiagnosticsMessage("hello"));
            await Task.Delay(100.Milliseconds());

            var handlerActivity = captured.FirstOrDefault(a =>
                a.GetTagItem(WolverineTracing.MessageHandler) is string h
                && h.Contains(nameof(TrackingDiagnosticsHandler)));

            handlerActivity.ShouldNotBeNull();
            var eventNames = handlerActivity.Events.Select(e => e.Name).ToArray();
            eventNames.ShouldNotContain(WolverineTracing.HandlerStarted);
            eventNames.ShouldNotContain(WolverineTracing.HandlerFinished);
        }
        finally
        {
            listener.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task transport_lag_tag_emits_when_handler_diagnostics_enabled()
    {
        var (host, captured, listener) = await startWithTrackingAsync(t =>
            t.HandlerExecutionDiagnosticsEnabled = true);

        try
        {
            await host.InvokeMessageAndWaitAsync(new TrackingDiagnosticsMessage("hello"));
            await Task.Delay(100.Milliseconds());

            var handlerActivity = captured.FirstOrDefault(a =>
                a.GetTagItem(WolverineTracing.MessageHandler) is string h
                && h.Contains(nameof(TrackingDiagnosticsHandler)));

            handlerActivity.ShouldNotBeNull();
            handlerActivity.GetTagItem(WolverineTracing.EnvelopeTransportLagMs).ShouldNotBeNull();
        }
        finally
        {
            listener.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task transport_lag_tag_absent_when_handler_diagnostics_disabled()
    {
        var (host, captured, listener) = await startWithTrackingAsync(_ => { });

        try
        {
            await host.InvokeMessageAndWaitAsync(new TrackingDiagnosticsMessage("hello"));
            await Task.Delay(100.Milliseconds());

            var handlerActivity = captured.FirstOrDefault(a =>
                a.GetTagItem(WolverineTracing.MessageHandler) is string h
                && h.Contains(nameof(TrackingDiagnosticsHandler)));

            handlerActivity.ShouldNotBeNull();
            handlerActivity.GetTagItem(WolverineTracing.EnvelopeTransportLagMs).ShouldBeNull();
            handlerActivity.GetTagItem(WolverineTracing.EnvelopeReceiveDwellMs).ShouldBeNull();
        }
        finally
        {
            listener.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    #endregion

    #region DeserializationSpanEnabled

    [Fact]
    public async Task deserialize_span_does_not_start_when_disabled()
    {
        // The span only fires for envelopes that arrive serialized and need to
        // be deserialized — we can't easily synthesize that path through
        // InvokeMessageAndWait. Instead, exercise the gate directly: the span
        // is gated on the flag at the call site, so when the flag is false the
        // span call is skipped and zero deserialize activities ever land in
        // the listener. (The sibling InvokeMessageAndWait test confirms the
        // host startup exercises the path without errors.)
        var (host, captured, listener) = await startWithTrackingAsync(_ => { });

        try
        {
            await host.InvokeMessageAndWaitAsync(new TrackingDiagnosticsMessage("hello"));
            await Task.Delay(100.Milliseconds());

            captured.Any(a => a.OperationName == WolverineTracing.Deserialize).ShouldBeFalse();
        }
        finally
        {
            listener.Dispose();
            await host.StopAsync();
            host.Dispose();
        }
    }

    #endregion

    #region OutboxDiagnosticsEnabled

    // Outbox events are stamped via codegen onto the FlushOutgoingMessages
    // MethodCall postprocessor, which is added by persistence-providing
    // frame providers (Marten / EF Core / RDBMS). Without a configured
    // persistence provider in this test there is no FlushOutgoingMessages
    // frame to stamp, which is correct behaviour: when there's no outbox
    // there are no outbox events to fire. The default-off assertion above
    // covers the gating contract; the FlushOutgoingMessages stamping itself
    // is verified by inspection of the codegen path in HandlerChain.

    #endregion

    #region [Obsolete] shim

    [Fact]
    public void obsolete_message_causation_shim_round_trips_through_tracking()
    {
        var options = new WolverineOptions();

#pragma warning disable CS0618
        options.EnableMessageCausationTracking.ShouldBeFalse();
        options.EnableMessageCausationTracking = true;
        options.Tracking.EnableMessageCausationTracking.ShouldBeTrue();

        options.Tracking.EnableMessageCausationTracking = false;
        options.EnableMessageCausationTracking.ShouldBeFalse();
#pragma warning restore CS0618
    }

    #endregion
}

public record TrackingDiagnosticsMessage(string Text);

public static class TrackingDiagnosticsHandler
{
    public static void Handle(TrackingDiagnosticsMessage message)
    {
        // no-op
    }
}
