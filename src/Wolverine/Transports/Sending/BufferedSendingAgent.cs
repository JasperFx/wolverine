using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;

namespace Wolverine.Transports.Sending;

internal class BufferedSendingAgent : SendingAgent
{
    private List<Envelope> _queued = new();

    public BufferedSendingAgent(ILogger logger, IMessageTracker messageLogger, ISender sender,
        DurabilitySettings settings, Endpoint endpoint) : base(logger, messageLogger, sender, settings, endpoint)
    {
    }

    public override bool IsDurable => false;

    public override Task EnqueueForRetryAsync(OutgoingMessageBatch batch)
    {
        _queued.AddRange(batch.Messages);
        _queued.RemoveAll(e => e.IsExpired());

        if (_queued.Count > Endpoint.MaximumEnvelopeRetryStorage)
        {
            var toRemove = _queued.Count - Endpoint.MaximumEnvelopeRetryStorage;
            _queued = _queued.Skip(toRemove).ToList();
        }

        return Task.CompletedTask;
    }

    protected override async Task afterRestartingAsync(ISender sender)
    {
        var toRetry = _queued.Where(x => !x.IsExpired()).ToArray();
        _queued.Clear();

        foreach (var envelope in toRetry) await _sending.PostAsync(envelope);
    }

    public override Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        return MarkSuccessAsync();
    }

    public override Task MarkSuccessfulAsync(Envelope outgoing)
    {
        return MarkSuccessAsync();
    }

    protected override async Task storeAndForwardAsync(Envelope envelope)
    {
        using var activity = Endpoint.TelemetryEnabled ? WolverineTracing.StartSending(envelope) : null;
        try
        {
            await _sending.PostAsync(envelope);
        }
        finally
        {
            activity?.Stop();
        }
    }
}