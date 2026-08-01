using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CoreTests.Transports;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// CritterWatch GH-907: a monitoring tool must not measurably inflate the thing it is monitoring. An idle
/// Wolverine app with CritterWatch applied reported ~150 messages/minute of nothing but its own agent
/// commands and monitoring traffic, because (a) the meter counters were unconditional, (b) the accumulator
/// guard was a string match that knew nothing of control endpoints, and (c) only IAgentCommand — not the
/// full system-message surface — was excluded from the CritterWatch publishing trackers. The fix resolves a
/// metrics-silent tracker ONCE at each construction site (endpoint agents, pipelines, executors); these
/// tests pin that selection and the resulting meter silence.
/// </summary>
public class system_traffic_metrics_silencing : IAsyncLifetime
{
    private const string TheServiceName = "system-traffic-silencing";

    private IHost _host = null!;
    private WolverineRuntime _runtime = null!;
    private MeterListener _listener = null!;
    private readonly ConcurrentDictionary<string, int> _counts = new();

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = TheServiceName;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<SilencingProbeHandler>();
            }).StartAsync();

        _runtime = (WolverineRuntime)_host.Services.GetService(typeof(IWolverineRuntime))!;

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Wolverine:" + TheServiceName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<int>((inst, _, _, _) => record(inst));
        _listener.SetMeasurementEventCallback<long>((inst, _, _, _) => record(inst));
        _listener.SetMeasurementEventCallback<double>((inst, _, _, _) => record(inst));
        _listener.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private void record(Instrument instrument)
    {
        _counts.AddOrUpdate(instrument.Name, 1, (_, count) => count + 1);
    }

    private static Endpoint appEndpoint()
        => new FakeEndpoint("fake://app".ToUri(), EndpointRole.Application);

    private static Endpoint systemEndpoint()
        => new FakeEndpoint("fake://control".ToUri(), EndpointRole.System);

    private Envelope envelopeFor(object message) => new()
    {
        Message = message,
        Destination = "fake://somewhere".ToUri(),
        MessageType = message.GetType().FullName
    };

    /*** TRACKER SELECTION — decided once at construction, never per envelope ***/

    [Fact]
    public void system_role_endpoints_get_the_metrics_silent_tracker()
    {
        _runtime.MessageTrackingFor(systemEndpoint())
            .ShouldBeOfType<WolverineRuntime.SystemTrafficMessageTracker>();
    }

    [Fact]
    public void telemetry_disabled_endpoints_get_the_metrics_silent_tracker()
    {
        var endpoint = appEndpoint();
        endpoint.TelemetryEnabled = false;

        _runtime.MessageTrackingFor(endpoint)
            .ShouldBeOfType<WolverineRuntime.SystemTrafficMessageTracker>();
    }

    [Fact]
    public void application_endpoints_keep_the_standard_tracker()
    {
        _runtime.MessageTrackingFor(appEndpoint()).ShouldBeSameAs(_runtime.MessageTracking);
    }

    [Fact]
    public void agent_commands_execute_metrics_silent_even_on_an_application_endpoint()
    {
        // The message-type half of the gate: an agent command handled on ANY endpoint is system traffic.
        var executor = ((IExecutorFactory)_runtime).BuildFor(typeof(CheckAgentHealth), appEndpoint());

        executor.ShouldBeOfType<Executor>().Tracker
            .ShouldBeOfType<WolverineRuntime.SystemTrafficMessageTracker>();
    }

    [Fact]
    public void agent_commands_invoked_through_the_runtime_pipeline_are_metrics_silent()
    {
        var executor = ((IExecutorFactory)_runtime).BuildFor(typeof(CheckAgentHealth));

        executor.ShouldBeOfType<Executor>().Tracker
            .ShouldBeOfType<WolverineRuntime.SystemTrafficMessageTracker>();
    }

    [Fact]
    public void application_messages_on_application_endpoints_keep_metrics()
    {
        var executor = ((IExecutorFactory)_runtime).BuildFor(typeof(SilencingProbeMessage), appEndpoint());

        executor.ShouldBeOfType<Executor>().Tracker.ShouldBeSameAs(_runtime.MessageTracking);
    }

    [Fact]
    public void promoting_an_endpoint_to_node_control_duty_marks_it_as_system()
    {
        // UseTcpForControlEndpoint promotes a plain TCP endpoint; before GH-907 it stayed
        // Application-role and its agent-command traffic was counted as application volume.
        var options = new WolverineOptions();
        var endpoint = new TcpEndpoint(577);
        endpoint.Role.ShouldBe(EndpointRole.Application);

        options.Transports.NodeControlEndpoint = endpoint;

        endpoint.Role.ShouldBe(EndpointRole.System);
    }

    /*** METER BEHAVIOR — the silent tracker records nothing, the standard one records ***/

    [Fact]
    public void the_silent_tracker_reaches_no_meter_instrument()
    {
        _counts.Clear();
        var tracker = _runtime.MessageTrackingFor(systemEndpoint());
        var envelope = envelopeFor(new CheckAgentHealth());

        tracker.Sent(envelope);
        tracker.Received(envelope);
        tracker.ExecutionStarted(envelope);
        tracker.ExecutionFinished(envelope);
        tracker.MessageSucceeded(envelope);
        tracker.MessageFailed(envelope, new InvalidOperationException("boom"));

        _listener.RecordObservableInstruments();
        _counts.ShouldBeEmpty();
    }

    [Fact]
    public void the_standard_tracker_still_records()
    {
        _counts.Clear();
        var envelope = envelopeFor(new SilencingProbeMessage());

        _runtime.MessageTracking.Sent(envelope);

        _counts.ContainsKey(MetricsConstants.MessagesSent).ShouldBeTrue();
    }
}

public record SilencingProbeMessage;

public class SilencingProbeHandler
{
    public void Handle(SilencingProbeMessage _)
    {
    }
}
