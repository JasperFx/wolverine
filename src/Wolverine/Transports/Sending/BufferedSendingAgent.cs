using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
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

    public BufferedSendingAgent(ILogger logger, IMessageTracker messageLogger, ISender sender,
        DurabilitySettings settings, Endpoint endpoint, IWolverineRuntime? runtime,
        SendingFailurePolicies? sendingFailurePolicies) : base(logger, messageLogger, sender, settings, endpoint, runtime, sendingFailurePolicies)
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
            _queued.RemoveRange(0, toRemove);
        }

        return Task.CompletedTask;
    }

    protected override async Task afterRestartingAsync(ISender sender)
    {
        var queued = _queued;
        _queued = new();

        for (var i = 0; i < queued.Count; i++)
        {
            var envelope = queued[i];
            if (!envelope.IsExpired())
            {
                await _sending.PostAsync(envelope);
            }
        }
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