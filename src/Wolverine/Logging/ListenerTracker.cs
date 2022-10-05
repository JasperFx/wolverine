using System;
using System.Threading.Tasks;
using Baseline;
using Wolverine.Util;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Logging;



public class ListenerTracker : WatchedObservable<ListenerState>, IObserver<ListenerState>
{
    private readonly LightweightCache<Uri, ListenerState> _states
        = new(uri => new ListenerState(uri, uri.ToString(), ListeningStatus.Unknown));

    private readonly LightweightCache<string, ListenerState> _byName
        = new(name => new ListenerState("unknown://".ToUri(), name, ListeningStatus.Unknown));

    internal ListenerTracker(ILogger logger) : base(logger)
    {
        Subscribe(this);
    }

    /// <summary>
    /// Current status of a listener by endpoint 
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    public ListeningStatus StatusFor(Uri uri)
    {
        return _states[uri].Status;
    }

    /// <summary>
    /// Current status of a listener by the endpoint name
    /// </summary>
    /// <param name="endpointState"></param>
    /// <returns></returns>
    public ListeningStatus StatusFor(string endpointState)
    {
        return _byName[endpointState].Status;
    }
    
    public Task WaitForListenerStatusAsync(string endpointName, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = _byName[endpointName];
        if (listenerState.Status == status) return Task.CompletedTask;

        var waiter = new StatusWaiter(new ListenerState(listenerState.Uri, endpointName, status), this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }

    public Task WaitForListenerStatusAsync(Uri uri, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = _states[uri];
        if (listenerState.Status == status) return Task.CompletedTask;

        var waiter = new StatusWaiter(new ListenerState(listenerState.Uri, listenerState.EndpointName, status), this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }


    void IObserver<ListenerState>.OnCompleted()
    {
        // nothing
    }

    void IObserver<ListenerState>.OnError(Exception error)
    {
        // nothing
    }

    void IObserver<ListenerState>.OnNext(ListenerState value)
    {
        _states[value.Uri] = value;
        _byName[value.EndpointName] = value;
    }

}

public record ListenerState(Uri Uri, string EndpointName, ListeningStatus Status);
