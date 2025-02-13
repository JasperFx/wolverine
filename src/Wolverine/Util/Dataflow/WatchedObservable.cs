using System.Collections.Immutable;
using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;

namespace Wolverine.Util.Dataflow;

// TODO -- move this to JasperFx
public class WatchedObservable<T> : IObservable<T>, IDisposable
{
    private readonly ActionBlock<T> _block;
    private readonly ILogger _logger;
    private ImmutableList<IObserver<T>> _listeners = ImmutableList<IObserver<T>>.Empty;

    public WatchedObservable(ILogger logger)
    {
        _logger = logger;
        _block = new ActionBlock<T>(publish, new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1
        });
    }

    public virtual void Dispose()
    {
        // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
        _listeners.Clear();
        _block.Complete();
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        if (!_listeners.Contains(observer))
        {
            _listeners = _listeners.Add(observer);
        }

        return new Unsubscriber(this, observer);
    }

    internal void Publish(T state)
    {
        _block.Post(state);
    }

    private void publish(T state)
    {
        foreach (var observer in _listeners)
        {
            try
            {
                observer.OnNext(state);
            }
            catch (Exception e)
            {
                // log them, but never let it through
                _logger.LogError(e, "Failed to notify subscriber {Subscriber} of listener state {State}", observer,
                    state);
            }
        }
    }

    internal Task DrainAsync()
    {
        _block.Complete();
        return _block.Completion;
    }

    private class Unsubscriber : IDisposable
    {
        private readonly IObserver<T> _observer;
        private readonly WatchedObservable<T> _tracker;

        public Unsubscriber(WatchedObservable<T> tracker, IObserver<T> observer)
        {
            _tracker = tracker;
            _observer = observer;
        }

        public void Dispose()
        {
            _tracker._listeners = _tracker._listeners.Remove(_observer);
        }
    }
}