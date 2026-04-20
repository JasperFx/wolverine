using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Transports.Local;

/// <summary>
/// Pseudo-listener used when the durability agent recovers persisted incoming envelopes
/// and dispatches them to a non-durable local queue (BufferedLocalQueue). The local queue
/// path goes through <see cref="IReceiver.ReceivedAsync(IListener, Envelope[])"/>, which
/// uses <c>envelope.Listener</c> as the channel callback for completion. By wrapping the
/// recovered envelope with this listener, we ensure that successful handling marks the
/// inbox row as <see cref="EnvelopeStatus.Handled"/> in the database — without this, the
/// row stays in <c>wolverine_incoming</c> forever and gets reprocessed on every host
/// restart. See https://github.com/JasperFx/wolverine/issues/1942.
/// </summary>
internal sealed class LocalQueueRecoveryListener : IListener
{
    private readonly IWolverineRuntime _runtime;

    public LocalQueueRecoveryListener(Uri address, IWolverineRuntime runtime)
    {
        Address = address;
        _runtime = runtime;
    }

    public Uri Address { get; }

    public IHandlerPipeline? Pipeline => null;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        try
        {
            await _runtime.Storage.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
        }
        catch (Exception e)
        {
            _runtime.Logger.LogError(e,
                "Error trying to mark recovered envelope {Id} as handled in the transactional inbox",
                envelope.Id);
        }
    }

    // If the handler defers, leave the inbox row alone — it will be picked up by the
    // durability agent again on the next ownership reset.
    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask StopAsync() => ValueTask.CompletedTask;
}
