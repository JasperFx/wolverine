namespace Wolverine.Util.Dataflow;

internal abstract class ConditionalWaiter<T> : IObserver<T>
{
    private readonly TaskCompletionSource<T> _completion;
    private readonly IDisposable _unsubscribe;

    public ConditionalWaiter(IObservable<T> parent, TimeSpan timeout)
    {
        _completion = new TaskCompletionSource<T>();


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

    public void OnNext(T value)
    {
        if (!hasCompleted(value))
        {
            return;
        }

        _completion.SetResult(value);
        _unsubscribe.Dispose();
    }

    protected abstract bool hasCompleted(T state);
}