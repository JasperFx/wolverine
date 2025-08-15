using JasperFx.Blocks;
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
    private readonly SemaphoreSlim _queueLock = new SemaphoreSlim(1, 1);

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

    public override bool IsDurable => true;

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

        await _queueLock.WaitAsync();
        try
        {
            var (expiredInQueue, notExpiredInQueue) = SplitByExpiration(_queued);
            var (expiredInBatch, notExpiredInBatch) = SplitByExpiration(batch.Messages);

            var expired = expiredInBatch.Concat(expiredInQueue).ToArray();
            var all = notExpiredInBatch.Concat(notExpiredInQueue).ToList();

            var (retained, reassigned) = TrimToMaxCapacity(all, Endpoint.MaximumEnvelopeRetryStorage);

            await executeWithRetriesAsync(async () =>
            {
                await _outbox.DiscardAndReassignOutgoingAsync(expired, reassigned, TransportConstants.AnyNode);
                _logger.DiscardedExpired(expired);
            });

            _queued = retained;
        }
        finally
        {
            _queueLock.Release();
        }

        static (Envelope[] expired, Envelope[] notExpired) SplitByExpiration(IEnumerable<Envelope> messages)
        {
            var lookup = messages.ToLookup(x => x.IsExpired());
            return (lookup[true].ToArray(), lookup[false].ToArray());
        }

        static (List<Envelope> retained, Envelope[] reassigned) TrimToMaxCapacity(
            List<Envelope> messages, int maxCapacity)
        {
            return messages.Count <= maxCapacity
                ? (messages, Array.Empty<Envelope>())
                : (messages.Take(maxCapacity).ToList(), messages.Skip(maxCapacity).ToArray());
        }
    }

    protected override async Task afterRestartingAsync(ISender sender)
    {
        Envelope[] toRetry;
        await _queueLock.WaitAsync();
        try
        {
            var lookup = _queued.ToLookup(x => x.IsExpired());
            var expired = lookup[true].ToArray();
            toRetry = lookup[false].ToArray();

            if (expired.Length != 0)
            {
                await executeWithRetriesAsync(() => _outbox.DeleteOutgoingAsync(expired));
            }

            _queued.Clear();
        }
        finally
        {
            _queueLock.Release();
        }

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
        using var activity = Endpoint.TelemetryEnabled ? WolverineTracing.StartSending(envelope) : null;
        await _storeAndForward.PostAsync(envelope);
        activity?.Stop();
    }
}