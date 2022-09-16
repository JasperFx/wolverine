using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Baseline;
using Wolverine.Util;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.Logging;

public class ListenerTracker : IObservable<ListenerState>, IObserver<ListenerState>, IDisposable
{
    private readonly ILogger _logger;

    private readonly LightweightCache<Uri, ListenerState> _states
        = new(uri => new ListenerState(uri, uri.ToString(), ListeningStatus.Unknown));

    private readonly LightweightCache<string, ListenerState> _byName
        = new(name => new ListenerState("unknown://".ToUri(), name, ListeningStatus.Unknown));

    private ImmutableList<IObserver<ListenerState>> _listeners = ImmutableList<IObserver<ListenerState>>.Empty;
    private readonly IDisposable _subscription;
    private readonly ActionBlock<ListenerState> _block;

    public ListenerTracker(ILogger logger)
    {
        _logger = logger;
        _block = new ActionBlock<ListenerState>(publish, new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1,

        });

        _subscription = Subscribe(this);
    }

    internal void Publish(ListenerState state)
    {
        _block.Post(state);
    }

    private void publish(ListenerState state)
    {
        // TODO -- consider moving the logging here

        foreach (var observer in _listeners)
        {
            try
            {
                observer.OnNext(state);
            }
            catch (Exception e)
            {
                // log them, but never let it through
                _logger.LogError(e, "Failed to notify subscriber {Subscriber} of listener state {State}", observer, state);
            }
        }
    }

    public ListeningStatus StatusFor(Uri uri)
    {
        return _states[uri].Status;
    }

    public ListeningStatus StatusFor(string endpointState)
    {
        return _byName[endpointState].Status;
    }

    public IDisposable Subscribe(IObserver<ListenerState> observer)
    {
        if (!_listeners.Contains(observer)) _listeners = _listeners.Add(observer);

        return new Unsubscriber(this, observer);
    }

    private class Unsubscriber: IDisposable
    {
        private readonly IObserver<ListenerState> _observer;
        private readonly ListenerTracker _tracker;

        public Unsubscriber(ListenerTracker tracker, IObserver<ListenerState> observer)
        {
            _tracker = tracker;
            _observer = observer;
        }

        public void Dispose()
        {
            if (_observer != null) _tracker._listeners = _tracker._listeners.Remove(_observer);
        }
    }

    public Task WaitForListenerStatus(string endpointName, ListeningStatus status, TimeSpan timeout)
    {
        var listenerState = _byName[endpointName];
        if (listenerState.Status == status) return Task.CompletedTask;

        var waiter = new StatusWaiter(new ListenerState(listenerState.Uri, endpointName, status), this, timeout);
        Subscribe(waiter);

        return waiter.Completion;
    }

    public Task WaitForListenerStatus(Uri uri, ListeningStatus status, TimeSpan timeout)
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

    void IDisposable.Dispose()
    {
        _subscription.Dispose();
        _block.Complete();
    }
}

internal class StatusWaiter : IObserver<ListenerState>
{
    private readonly IDisposable _unsubscribe;
    private readonly Func<ListenerState, bool> _condition;
    private readonly TaskCompletionSource<ListenerState> _completion;

    public StatusWaiter(ListenerState expected, ListenerTracker parent, TimeSpan timeout)
    {
        _condition = x => (x.EndpointName == expected.EndpointName || x.Uri == expected.Uri) && x.Status == expected.Status;
        _completion = new TaskCompletionSource<ListenerState>();


        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                "Listener {Expected} did not change to status {expected.Status} in the time allowed"));
        });

        _unsubscribe = parent.Subscribe(this);
    }

    public Task Completion => _completion.Task;

    public void OnCompleted()
    {
        // nothing
    }

    public void OnError(Exception error)
    {
        _completion.SetException(error);
    }

    public void OnNext(ListenerState value)
    {
        if (_condition(value))
        {
            _completion.SetResult(value);
            _unsubscribe.Dispose();
        }
    }
}

public record ListenerState(Uri Uri, string EndpointName, ListeningStatus Status);
