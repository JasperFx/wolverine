using JasperFx.Core;
using Wolverine.Runtime;

namespace Wolverine.Transports;

public class ParallelListener : IListener, IDisposable
{
    private readonly List<IListener> _listeners = new();

    public ParallelListener(Uri address, IEnumerable<IListener> listeners)
    {
        Address = address;
        _listeners.AddRange(listeners);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners.OfType<IDisposable>()) listener.SafeDispose();
    }

    // Really only for retries anyway
    public IHandlerPipeline? Pipeline => _listeners.First().Pipeline;

    // These should never be called, but still
    public ValueTask CompleteAsync(Envelope envelope) =>
        envelope.Listener!.CompleteAsync(envelope);

    public ValueTask DeferAsync(Envelope envelope) =>
        envelope.Listener!.DeferAsync(envelope);

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        foreach (var listener in _listeners) await listener.StopAsync();
    }

    public ValueTask DisposeAsync() =>
        _listeners.MaybeDisposeAllAsync();
}