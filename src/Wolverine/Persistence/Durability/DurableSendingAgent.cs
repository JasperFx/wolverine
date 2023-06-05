using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util.Dataflow;

namespace Wolverine.Persistence.Durability;

internal class DurableSendingAgent : SendingAgent
{
    private readonly RetryBlock<Envelope[]> _deleteOutgoingMany;
    private readonly RetryBlock<Envelope> _deleteOutgoingOne;
    private readonly RetryBlock<OutgoingMessageBatch> _enqueueForRetry;
    private readonly ILogger _logger;
    private readonly IMessageOutbox _outbox;
    private readonly RetryBlock<Envelope> _storeAndForward;

    private IList<Envelope> _queued = new List<Envelope>();

    public DurableSendingAgent(ISender sender, DurabilitySettings settings, ILogger logger,
        IMessageTracker messageLogger,
        IMessageStore persistence, Endpoint endpoint) : base(logger, messageLogger, sender, settings, endpoint)
    {
        _logger = logger;

        _outbox = persistence.Outbox;

        _deleteOutgoingOne =
            new RetryBlock<Envelope>((e, _) => _outbox.DeleteOutgoingAsync(e), logger, settings.Cancellation);

        _deleteOutgoingMany = new RetryBlock<Envelope[]>((envelopes, _) => _outbox.DeleteOutgoingAsync(envelopes),
            logger, settings.Cancellation);

        _enqueueForRetry = new RetryBlock<OutgoingMessageBatch>((batch, _) => enqueueForRetryAsync(batch), _logger,
            _settings.Cancellation);

        _storeAndForward = new RetryBlock<Envelope>(async (e, _) =>
        {
            await _outbox.StoreOutgoingAsync(e, _settings.AssignedNodeNumber);

            await _sending.PostAsync(e);
        }, _logger, settings.Cancellation);
    }

    public override bool IsDurable { get; } = true;

    protected override async Task drainOtherAsync()
    {
        await _deleteOutgoingMany.DrainAsync();
        await _deleteOutgoingOne.DrainAsync();
        await _enqueueForRetry.DrainAsync();
        await _storeAndForward.DrainAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _deleteOutgoingMany.Dispose();
        _deleteOutgoingOne.Dispose();
        _enqueueForRetry.Dispose();
        _storeAndForward.Dispose();
    }

    public override Task EnqueueForRetryAsync(OutgoingMessageBatch batch)
    {
        return _enqueueForRetry.PostAsync(batch);
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

        await executeWithRetriesAsync(async () =>
        {
            await _outbox.DiscardAndReassignOutgoingAsync(expired, reassigned, TransportConstants.AnyNode);
            _logger.DiscardedExpired(expired);
        });

        _queued = all.Take(Endpoint.MaximumEnvelopeRetryStorage).ToList();
    }


    protected override async Task afterRestartingAsync(ISender sender)
    {
        var expired = _queued.Where(x => x.IsExpired()).ToArray();
        if (expired.Any())
        {
            await executeWithRetriesAsync(() => _outbox.DeleteOutgoingAsync(expired));
        }

        var toRetry = _queued.Where(x => !x.IsExpired()).ToArray();
        _queued = new List<Envelope>();

        foreach (var envelope in toRetry) await _sending.PostAsync(envelope);
    }

    public override Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        return _deleteOutgoingMany.PostAsync(outgoing.Messages.ToArray());
    }

    public override Task MarkSuccessfulAsync(Envelope outgoing)
    {
        return _deleteOutgoingOne.PostAsync(outgoing);
    }

    protected override async Task storeAndForwardAsync(Envelope envelope)
    {
        using var activity = WolverineTracing.StartSending(envelope);
        await _storeAndForward.PostAsync(envelope);
        activity?.Stop();
    }
}