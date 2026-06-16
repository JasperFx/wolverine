using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Wolverine.Logging;
using Wolverine.Transports;

namespace CircuitBreakingTests;

public class ListenerObserver(ILogger<ListenerObserver> logger)
    : IObserver<IWolverineEvent>
{
    private readonly ConcurrentQueue<ListenerState> _recordedStates = [];

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void OnNext(IWolverineEvent value)
    {
        if (value is ListenerState state)
        {
            logger.LogDebug("CB status: {Status}", state.Status);
            _recordedStates.Enqueue(state);
        }
    }

    public ListeningStatus[] RecordedStates => [.. _recordedStates.Select(x => x.Status)];
}