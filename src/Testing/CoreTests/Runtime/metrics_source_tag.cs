using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

// GH-3221: the `source` (service name) tag must be present on EVERY Wolverine metric instrument, not just
// wolverine-messages-sent / -received, so a shared metrics backend can slice each series per service.
public class metrics_source_tag : IAsyncLifetime
{
    private const string TheServiceName = "metrics-source-test";

    private IHost _host = null!;
    private MeterListener _listener = null!;

    // instrument name -> list of tag dictionaries captured for each recorded measurement
    private readonly ConcurrentDictionary<string, ConcurrentBag<Dictionary<string, object?>>> _measurements = new();

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = TheServiceName;

                // A matching exception is moved to the error queue on the first failure, which drives the
                // execution-failure + dead-letter-queue instruments.
                opts.OnException<InvalidOperationException>().MoveToErrorQueue();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MetricSuccessHandler>()
                    .IncludeType<MetricFailHandler>();
            }).StartAsync();

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

        _listener.SetMeasurementEventCallback<int>((inst, _, tags, _) => Record(inst, tags));
        _listener.SetMeasurementEventCallback<long>((inst, _, tags, _) => Record(inst, tags));
        _listener.SetMeasurementEventCallback<double>((inst, _, tags, _) => Record(inst, tags));
        _listener.Start();
    }

    public async Task DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        _measurements.GetOrAdd(instrument.Name, _ => new ConcurrentBag<Dictionary<string, object?>>()).Add(dict);
    }

    [Fact]
    public async Task every_instrument_carries_the_source_tag()
    {
        // Success path -> execution-time, messages-succeeded, effective-time.
        await _host.TrackActivity().SendMessageAndWaitAsync(new MetricSuccess());

        // Failure path -> execution-failure, dead-letter-queue (+ effective-time).
        await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new MetricFail());

        // The instruments this issue is specifically about must all have recorded.
        foreach (var name in new[]
                 {
                     MetricsConstants.ExecutionTime,
                     MetricsConstants.MessagesSucceeded,
                     MetricsConstants.EffectiveMessageTime,
                     MetricsConstants.MessagesFailed,
                     MetricsConstants.DeadLetterQueue
                 })
        {
            _measurements.ContainsKey(name).ShouldBeTrue($"instrument {name} did not record");
        }

        // Invariant: NO Wolverine instrument records a measurement without the source tag.
        foreach (var (name, records) in _measurements)
        {
            foreach (var tags in records)
            {
                tags.ContainsKey(MetricsConstants.SourceKey).ShouldBeTrue($"instrument {name} missing source");
                tags[MetricsConstants.SourceKey].ShouldBe(TheServiceName, $"instrument {name} wrong source");
            }
        }
    }
}

public record MetricSuccess;

public record MetricFail;

public class MetricSuccessHandler
{
    public async Task Handle(MetricSuccess _)
    {
        // Ensure a measurable, non-zero execution time so the wolverine-execution-time histogram actually
        // records — ExecutionFinished only records when StopTiming() (truncated to whole milliseconds) is
        // > 0. Without this the histogram has no data points on a fast machine, which made the source-tag
        // and bucket-boundary tests flaky in CI.
        await Task.Delay(5);
    }
}

public class MetricFailHandler
{
    public void Handle(MetricFail _) => throw new InvalidOperationException("boom");
}
