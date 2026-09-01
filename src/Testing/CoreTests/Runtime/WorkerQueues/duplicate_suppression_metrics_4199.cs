using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
/// GH-4199, item 3. The plain counters on the guard prove the arithmetic; this proves the number actually
/// leaves the process. A monitoring consumer reads the OTel instrument, not an internal property, and the
/// guard is only ever handed a Meter from <see cref="Endpoint.Compile"/> -- so without this the wiring could
/// be silently absent while every unit test still passed.
/// </summary>
public class duplicate_suppression_metrics_4199 : IAsyncLifetime
{
    private const string TheServiceName = "dup-metrics-4199";

    private IHost _host = null!;
    private MeterListener _listener = null!;
    private readonly ConcurrentDictionary<string, ConcurrentBag<Dictionary<string, object?>>> _measurements = new();

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = TheServiceName;
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync(TestContext.Current.CancellationToken);

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

        _listener.SetMeasurementEventCallback<int>((inst, _, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            _measurements.GetOrAdd(inst.Name, _ => new ConcurrentBag<Dictionary<string, object?>>()).Add(dict);
        });

        _listener.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void the_suppression_counter_reaches_the_meter_tagged_by_endpoint()
    {
        var runtime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = new NativeAckStubEndpoint("dup-4199", new StubTransport()) { IsListener = true };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.InMemoryIdempotency = new InMemoryIdempotencySettings();
        endpoint.Compile(runtime);

        var guard = endpoint.IdempotencyGuard.ShouldNotBeNull();

        var envelope = new Envelope { Id = Guid.NewGuid(), Destination = endpoint.Uri };

        guard.TryBeginProcessing(envelope).ShouldBeTrue();
        guard.TryBeginProcessing(envelope).ShouldBeFalse();

        _measurements.TryGetValue(MetricsConstants.DuplicatesSuppressed, out var recorded).ShouldBeTrue(
            "The duplicate-suppression counter never reached the meter, so a monitoring consumer cannot see it at all.");

        recorded!.Count.ShouldBe(1);

        // Tagged by endpoint, so a fleet-wide redelivery rate can be broken down per listener rather than
        // arriving as one undifferentiated number.
        recorded.Single()[MetricsConstants.MessageDestinationKey].ShouldBe(endpoint.Uri.ToString());
    }
}
