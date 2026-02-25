using System.Collections.Immutable;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Top-level coordinator for message handling metrics collection. Maintains an immutable
/// collection of <see cref="MessageTypeMetricsAccumulator"/> instances, one per unique
/// (message type, destination) pair. Runs a background loop that periodically (every
/// <c>WolverineOptions.Metrics.SamplingPeriod</c>, default 5 seconds) triggers each
/// accumulator to export its snapshot via <see cref="IWolverineObserver.MessageHandlingMetricsExported"/>.
/// Only snapshots with at least one tenant data point are published.
/// </summary>
// TODO -- make this lazy on WolverineRuntime
public class MetricsAccumulator : IAsyncDisposable
{
    private readonly IWolverineRuntime _runtime;
    private readonly object _syncLock = new();

    private ImmutableArray<MessageTypeMetricsAccumulator> _accumulators = ImmutableArray<MessageTypeMetricsAccumulator>.Empty;
    private Task _runner;

    /// <summary>
    /// Creates a new metrics accumulator bound to the given runtime.
    /// </summary>
    /// <param name="runtime">The Wolverine runtime providing cancellation, options, and observer access.</param>
    public MetricsAccumulator(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Finds or creates a <see cref="MessageTypeMetricsAccumulator"/> for the given message type
    /// and endpoint. Uses lock-free reads with a fallback to a locked creation path.
    /// </summary>
    /// <param name="messageTypeName">The fully-qualified CLR message type name.</param>
    /// <param name="endpoint">The endpoint whose URI identifies the destination.</param>
    /// <returns>The accumulator for the given message type and destination.</returns>
    public MessageTypeMetricsAccumulator FindAccumulator(string messageTypeName, Endpoint endpoint)
    {
        var endpointUri = endpoint.Uri;
        return FindAccumulator(messageTypeName, endpointUri);
    }

    /// <summary>
    /// Finds or creates a <see cref="MessageTypeMetricsAccumulator"/> for the given message type
    /// and destination URI. Uses lock-free reads with a fallback to a locked creation path.
    /// </summary>
    /// <param name="messageTypeName">The fully-qualified CLR message type name.</param>
    /// <param name="endpointUri">The destination endpoint URI.</param>
    /// <returns>The accumulator for the given message type and destination.</returns>
    public MessageTypeMetricsAccumulator FindAccumulator(string messageTypeName, Uri endpointUri)
    {
        var snapshot = _accumulators;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var acc = snapshot[i];
            if (acc.MessageType == messageTypeName && acc.Destination == endpointUri)
            {
                return acc;
            }
        }

        lock (_syncLock)
        {
            snapshot = _accumulators;
            for (var i = 0; i < snapshot.Length; i++)
            {
                var acc = snapshot[i];
                if (acc.MessageType == messageTypeName && acc.Destination == endpointUri)
                {
                    return acc;
                }
            }

            var accumulator = new MessageTypeMetricsAccumulator(messageTypeName, endpointUri);
            _accumulators = _accumulators.Add(accumulator);
            return accumulator;
        }
    }

    /// <summary>
    /// Drains all pending batched metrics data points across all accumulators by waiting
    /// for their batching pipelines to complete.
    /// </summary>
    public async ValueTask DrainAsync()
    {
        var tasks = _accumulators.Select(x => x.EntryPoint.WaitForCompletionAsync());
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// The start time of the overall metrics collection. Set when the accumulator is created.
    /// </summary>
    public DateTimeOffset From { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Starts the background export loop. On each <c>SamplingPeriod</c> tick, iterates all
    /// accumulators, triggers export, and publishes non-empty snapshots to the runtime's
    /// <see cref="IWolverineObserver"/>.
    /// </summary>
    public void Start()
    {
        _runner = Task.Run(async () =>
        {
            try
            {
                while (!_runtime.Cancellation.IsCancellationRequested)
                {
                    await Task.Delay(_runtime.Options.Metrics.SamplingPeriod);
                    try
                    {
                        foreach (var accumulator in _accumulators)
                        {
                            var metrics = accumulator.TriggerExport(_runtime.DurabilitySettings.AssignedNodeNumber);

                            if (metrics.PerTenant.Length > 0)
                            {
                                _runtime.Observer.MessageHandlingMetricsExported(metrics);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _runtime.Logger.LogError(e, "Error trying to export message handler metrics");
                    }
                }
            }
            catch (OperationCanceledException e)
            {
                // Nothing
            }
        }, _runtime.Cancellation);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _runner.SafeDispose();
        return new ValueTask();
    }
}
