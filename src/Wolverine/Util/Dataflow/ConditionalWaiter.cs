namespace Wolverine.Util.Dataflow;

public sealed class ConditionalWaiter<TObservedEvent, TSpecificEvent> : IObserver<TObservedEvent>
    where TSpecificEvent : TObservedEvent
{
    private readonly TaskCompletionSource<TSpecificEvent> _completion;
    private readonly Func<TSpecificEvent, bool> _match;
    private readonly IDisposable _unsubscribe;

    public ConditionalWaiter(Func<TSpecificEvent, bool> match, IObservable<TObservedEvent> parent, TimeSpan timeout)
    {
        _match = match;
        _completion = new TaskCompletionSource<TSpecificEvent>(TaskCreationOptions.RunContinuationsAsynchronously);


        var timeout1 = new CancellationTokenSource(timeout);
        timeout1.Token.Register(() =>
        {
            _completion.TrySetException(new TimeoutException(
                "Did not reach the expected state or message in time"));
        });

        _unsubscribe = parent.Subscribe(this);
    }

    public Task<TSpecificEvent> Completion => _completion.Task;

    public void OnCompleted()
    {
        // nothing
    }

    public void OnError(Exception error)
    {
        _completion.SetException(error);
    }

    public void OnNext(TObservedEvent value)
    {
        if (value is TSpecificEvent e && _match(e))
        {
            _completion.SetResult(e);
            _unsubscribe.Dispose();
        }
    }
}