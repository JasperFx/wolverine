using System.Reflection;
using Wolverine.Persistence.Durability;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5073. Forwards every <see cref="IMessageStore"/> call to a real store, except that
/// <see cref="IMessageStore.LoadPageOfGloballyOwnedIncomingAsync"/> waits until a set number of callers have
/// all finished loading. Recovery commands sharing one of these load the same page before any of them can
/// claim it, which is the interleaving behind GH-5073, every time and without any sleeps.
/// </summary>
public class LoadBarrierMessageStore : DispatchProxy
{
    private IMessageStore _inner = null!;
    private int _expected;
    private int _loaded;
    private readonly TaskCompletionSource _allLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IMessageStore For(IMessageStore inner, int callers)
    {
        var store = Create<IMessageStore, LoadBarrierMessageStore>();
        var proxy = (LoadBarrierMessageStore)(object)store;
        proxy._inner = inner;
        proxy._expected = callers;
        return store;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IMessageStore.LoadPageOfGloballyOwnedIncomingAsync))
        {
            return loadThenWaitAsync((Uri)args![0]!, (int)args[1]!);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    private async Task<IReadOnlyList<Envelope>> loadThenWaitAsync(Uri listenerAddress, int limit)
    {
        var page = await _inner.LoadPageOfGloballyOwnedIncomingAsync(listenerAddress, limit);

        if (Interlocked.Increment(ref _loaded) >= _expected)
        {
            _allLoaded.TrySetResult();
        }

        await _allLoaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        return page;
    }
}
