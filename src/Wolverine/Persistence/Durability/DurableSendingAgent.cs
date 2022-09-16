using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

internal class DurableSendingAgent : SendingAgent
{
    private readonly ILogger _logger;
    private readonly IEnvelopePersistence _persistence;

    private IList<Envelope> _queued = new List<Envelope>();

    public DurableSendingAgent(ISender sender, AdvancedSettings settings, ILogger logger,
        IMessageLogger messageLogger,
        IEnvelopePersistence persistence, Endpoint endpoint) : base(logger, messageLogger, sender, settings, endpoint)
    {
        _logger = logger;

        _persistence = persistence;
    }

    private async Task executeWithRetriesAsync(Func<Task> action, CancellationToken cancellation)
    {
        var i = 0;
        while (true)
        {
            if (cancellation.IsCancellationRequested) return;

            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed while trying to enqueue a message batch for retries");
                if (cancellation.IsCancellationRequested) return;

                i++;
                await Task.Delay(i * 100, cancellation).ConfigureAwait(false);
            }
        }
    }

    public override bool IsDurable { get; } = true;

    public override Task EnqueueForRetryAsync(OutgoingMessageBatch batch)
    {
        return executeWithRetriesAsync(() => enqueueForRetryAsync(batch), _settings.Cancellation);
    }

    private async Task enqueueForRetryAsync(OutgoingMessageBatch batch)
    {
        if (_settings.Cancellation.IsCancellationRequested)
        {
            return;
        }

        var expiredInQueue = _queued.Where(x => x.IsExpired()).ToArray();
        var expiredInBatch = batch.Messages.Where(x => x.IsExpired()).ToArray();

        var expired = expiredInBatch.Concat(expiredInQueue).ToArray();
        var all = _queued.Where(x => !expiredInQueue.Contains(x))
            .Concat(batch.Messages.Where(x => !expiredInBatch.Contains(x)))
            .ToList();

        var reassigned = Array.Empty<Envelope>();
        if (all.Count > Endpoint.MaximumEnvelopeRetryStorage)
        {
            reassigned = all.Skip(Endpoint.MaximumEnvelopeRetryStorage).ToArray();
        }

        await _persistence.DiscardAndReassignOutgoingAsync(expired, reassigned, TransportConstants.AnyNode);
        _logger.DiscardedExpired(expired);

        _queued = all.Take(Endpoint.MaximumEnvelopeRetryStorage).ToList();
    }

    protected override async Task afterRestartingAsync(ISender sender)
    {
        var expired = _queued.Where(x => x.IsExpired()).ToArray();
        if (expired.Any())
        {
            await _persistence.DeleteIncomingEnvelopesAsync(expired);
        }

        var toRetry = _queued.Where(x => !x.IsExpired()).ToArray();
        _queued = new List<Envelope>();

        foreach (var envelope in toRetry) await _sender.SendAsync(envelope);
    }

    public override Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        return executeWithRetriesAsync(() => _persistence.DeleteOutgoingAsync(outgoing.Messages.ToArray()),
            _settings.Cancellation);
    }

    public override Task MarkSuccessfulAsync(Envelope outgoing)
    {
        return executeWithRetriesAsync(() => _persistence.DeleteOutgoingAsync(outgoing), _settings.Cancellation);
    }

    protected override async Task storeAndForwardAsync(Envelope envelope)
    {
        using var activity = WolverineTracing.StartSending(envelope);
        try
        {
            await _persistence.StoreOutgoingAsync(envelope, _settings.UniqueNodeId);

            await _senderDelegate(envelope);
        }
        finally
        {
            activity?.Stop();
        }
    }
}
