using System.Collections.Concurrent;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.Logging;

public interface IWolverineEvent
{
    void ModifyState(WolverineTracker tracker);
}

public class WolverineTracker : WatchedObservable<IWolverineEvent>, IObserver<IWolverineEvent>
{
    internal WolverineTracker(ILogger logger) : base(logger)
    {
        Subscribe(this);
    }

    internal LightweightCache<string, ListenerState> ListenerStateByName { get; }
        = new(name => new ListenerState("unknown://".ToUri(), name, ListeningStatus.Unknown));

    internal LightweightCache<Uri, ListenerState> ListenerStateByUri { get; }
        = new(uri => new ListenerState(uri, uri.ToString(), ListeningStatus.Unknown));

    void IObserver<IWolverineEvent>.OnCompleted()
    {
        // nothing
    }

    void IObserver<IWolverineEvent>.OnError(Exception error)
    {
        // nothing
    }

    void IObserver<IWolverineEvent>.OnNext(IWolverineEvent value)
    {
        value.ModifyState(this);
    }

    /// <summary>
    ///     Current status of a listener by endpoint
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    public ListeningStatus StatusFor(Uri uri)
    {
        return ListenerStateByUri[uri].Status;
    }

    /// <summary>
    ///     Current status of a listener by the endpoint name
    /// </summary>
    /// <param name="endpointState"></param>
    /// <returns></returns>
    public ListeningStatus StatusFor(string endpointState)
    {
        return ListenerStateByName[endpointState].Status;
    }

    public Task WaitForListenerStatusAsync(string endpointName, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = ListenerStateByName[endpointName];
        if (listenerState.Status == status)
        {
            return Task.CompletedTask;
        }

        var waiter =
            new ConditionalWaiter<IWolverineEvent, ListenerState>(
                state => state.EndpointName == endpointName || state.Status == status, this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }

    public Task WaitForListenerStatusAsync(Uri uri, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = ListenerStateByUri[uri];
        if (listenerState.Status == status)
        {
            return Task.CompletedTask;
        }

        var waiter =
            new ConditionalWaiter<IWolverineEvent, ListenerState>(state => state.Uri == uri || state.Status == status,
                this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }

}

public record ListenerState(Uri Uri, string EndpointName, ListeningStatus Status) : IWolverineEvent
{
    public void ModifyState(WolverineTracker tracker)
    {
        tracker.ListenerStateByUri[Uri] = this;
        tracker.ListenerStateByName[EndpointName] = this;
    }
}