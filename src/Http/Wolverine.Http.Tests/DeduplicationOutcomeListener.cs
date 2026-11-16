using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests;

public sealed class DeduplicationOutcomeListener : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentQueue<DeduplicationOutcomeMeasurement> _measurements = new();

    public DeduplicationOutcomeListener(IHost host)
    {
        var meter = host.Services.GetRequiredService<IWolverineRuntime>().Meter;

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) &&
                    instrument.Name == MetricsConstants.DeduplicationOutcomes)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
        {
            var measured = new Dictionary<string, object?>();
            foreach (var tag in tags) measured[tag.Key] = tag.Value;

            for (var i = 0; i < value; i++)
            {
                _measurements.Enqueue(new DeduplicationOutcomeMeasurement(
                    (string?)measured.GetValueOrDefault(MetricsConstants.DeduplicationOutcomeKey),
                    (string?)measured.GetValueOrDefault("http.route"),
                    (string?)measured.GetValueOrDefault("http.request.method")));
            }
        });

        _listener.Start();
    }

    public string?[] On(string route)
        => _measurements.Where(x => x.Route == route).Select(x => x.Outcome).ToArray();

    public IReadOnlyList<DeduplicationOutcomeMeasurement> All => _measurements.ToArray();

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Samples ASP.NET Core's request spans, which are otherwise created only when something listens to them, and
    /// keeps each one as it stops.
    /// </summary>
    public sealed class RequestSpans : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;

        public RequestSpans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _stopped.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public static string TraceParent(ActivityTraceId traceId) => $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";

        // The server stops the request span after it completes the response, so the span can still be open here
        public async Task<string[]> DeduplicationEventsIn(ActivityTraceId traceId)
        {
            var timeout = Stopwatch.StartNew();
            while (_stopped.All(x => x.TraceId != traceId) && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10);
            }

            var span = _stopped.SingleOrDefault(x => x.TraceId == traceId)
                       ?? throw new TimeoutException($"No request span with trace id {traceId} stopped within 5 seconds");

            return span.Events
                .Select(x => x.Name)
                .Where(x => x.StartsWith("wolverine.deduplication.", StringComparison.Ordinal))
                .ToArray();
        }

        public void Dispose() => _listener.Dispose();
    }
}

public record DeduplicationOutcomeMeasurement(string? Outcome, string? Route, string? Method);
