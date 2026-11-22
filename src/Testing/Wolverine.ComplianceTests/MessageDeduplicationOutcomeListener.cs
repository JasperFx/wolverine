using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5613. Collects what a metrics consumer sees on one host's <see cref="MetricsConstants.MessageDeduplicationOutcomes" />
/// counter.
/// </summary>
public sealed class MessageDeduplicationOutcomeListener : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentQueue<MessageDeduplicationOutcome> _measurements = new();

    public MessageDeduplicationOutcomeListener(IHost host)
    {
        var meter = host.Services.GetRequiredService<IWolverineRuntime>().Meter;

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter) &&
                    instrument.Name == MetricsConstants.MessageDeduplicationOutcomes)
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
                _measurements.Enqueue(new MessageDeduplicationOutcome(
                    (string?)measured.GetValueOrDefault(MetricsConstants.DeduplicationOutcomeKey),
                    (string?)measured.GetValueOrDefault(MetricsConstants.MessageTypeKey),
                    (string?)measured.GetValueOrDefault(MetricsConstants.MessageHandlerKey),
                    (string?)measured.GetValueOrDefault(MetricsConstants.SourceKey)));
            }
        });

        _listener.Start();
    }

    public IReadOnlyList<MessageDeduplicationOutcome> All => _measurements.ToArray();

    /// <summary>
    /// The outcomes counted for one handler type, in the order they were counted.
    /// </summary>
    public string?[] For(Type handlerType)
    {
        var handler = handlerType.FullNameInCode();
        return _measurements.Where(x => x.Handler == handler).Select(x => x.Outcome).ToArray();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// The <c>wolverine.deduplication.*</c> events on the spans of one message type, as each span stops.
    /// </summary>
    public sealed class MessageSpans : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;

        public MessageSpans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == WolverineTracing.ActivitySource.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => _stopped.Enqueue(activity)
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public async Task<string[]> DeduplicationEventsOf(string messageType, int expected)
        {
            var timeout = Stopwatch.StartNew();
            while (eventsOf(messageType).Length < expected && timeout.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10);
            }

            return eventsOf(messageType);
        }

        private string[] eventsOf(string messageType)
            => _stopped
                .Where(x => x.GetTagItem(WolverineTracing.MessageType) as string == messageType)
                .SelectMany(x => x.Events)
                .Select(x => x.Name)
                .Where(x => x.StartsWith("wolverine.deduplication.", StringComparison.Ordinal))
                .ToArray();

        public void Dispose() => _listener.Dispose();
    }
}

public record MessageDeduplicationOutcome(string? Outcome, string? MessageType, string? Handler, string? Source);
