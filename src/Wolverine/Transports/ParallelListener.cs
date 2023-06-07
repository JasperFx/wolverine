using JasperFx.Core;

namespace Wolverine.Transports;

public class ParallelListener : IListener, IDisposable
{
    private readonly List<IListener> _listeners = new();

    public ParallelListener(Uri address, IEnumerable<IListener> listeners)
    {
        Address = address;
        _listeners.AddRange(listeners);
    }

    // These should never be called, but still
    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await envelope.Listener!.CompleteAsync(envelope);
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await envelope.Listener!.DeferAsync(envelope);
    }

    public Uri Address { get; }
    public async ValueTask StopAsync()
    {
        foreach (var listener in _listeners)
        {
            await listener.StopAsync();
        }
    }

    public void Dispose()
    {
        foreach (var listener in _listeners.OfType<IDisposable>()) listener.SafeDispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _listeners.MaybeDisposeAllAsync();
    }
}