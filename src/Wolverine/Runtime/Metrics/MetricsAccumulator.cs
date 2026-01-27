using System.Collections.Immutable;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Runtime.Metrics;

// TODO -- make this lazy on WolverineRuntime
public class MetricsAccumulator : IAsyncDisposable
{
    private readonly IWolverineRuntime _runtime;
    private readonly object _syncLock = new();
    
    private ImmutableArray<MessageTypeMetricsAccumulator> _accumulators = ImmutableArray<MessageTypeMetricsAccumulator>.Empty;
    private Task _runner;

    public MetricsAccumulator(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public MessageTypeMetricsAccumulator FindAccumulator(string messageTypeName, Endpoint endpoint)
    {
        var endpointUri = endpoint.Uri;
        return FindAccumulator(messageTypeName, endpointUri);
    }

    public MessageTypeMetricsAccumulator FindAccumulator(string messageTypeName, Uri endpointUri)
    {
        var accumulator =
            _accumulators.FirstOrDefault(x => x.MessageType == messageTypeName && x.Destination == endpointUri);

        if (accumulator != null)
        {
            return accumulator;
        }

        lock (_syncLock)
        {
            accumulator =
                _accumulators.FirstOrDefault(x => x.MessageType == messageTypeName && x.Destination == endpointUri);

            if (accumulator != null)
            {
                return accumulator;
            }

            accumulator = new MessageTypeMetricsAccumulator(messageTypeName, endpointUri);
            _accumulators = _accumulators.Add(accumulator);
        }
        
        return accumulator;
    }

    public async ValueTask DrainAsync()
    {
        var tasks = _accumulators.Select(x => x.EntryPoint.WaitForCompletionAsync());
        await Task.WhenAll(tasks);
    }
    
    public DateTimeOffset From { get; private set; } = DateTimeOffset.UtcNow;

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
                        var bus = new MessageBus(_runtime);

                        foreach (var accumulator in _accumulators)
                        {
                            var metrics = accumulator.TriggerExport(_runtime.DurabilitySettings.AssignedNodeNumber);
                            
                            if (metrics.PerTenant.Length > 0)
                            {
                                await bus.PublishAsync(metrics);
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

    public ValueTask DisposeAsync()
    {
        _runner.SafeDispose();
        return new ValueTask();
    }
}