using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
///     Holds partial fragment sets until they are complete, then hands back the reassembled body.
/// </summary>
/// <remarks>
///     <para>
///     In memory and per listener. That is safe exactly when every fragment of one message reaches one
///     listener — a FIFO queue, a <c>GlobalPartitioning</c> listener, or a single listening node — and
///     best-effort otherwise, which is why <c>FragmentOversizedMessages()</c> documents those three
///     shapes.
///     </para>
///     <para>
///     <b>Fragments are not deleted from SQS until the whole set arrives.</b> A node that holds two of
///     three fragments and then crashes has told SQS nothing, so all three become visible again after
///     the visibility timeout and can be reassembled by whoever picks them up. Deleting eagerly would
///     turn a crash into permanent message loss.
///     </para>
/// </remarks>
internal class SqsFragmentReassembler
{
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, PartialMessage> _partials = new();
    private readonly object _locker = new();

    public SqsFragmentReassembler(TimeSpan timeout, ILogger logger)
    {
        _timeout = timeout;
        _logger = logger;
    }

    /// <summary>
    ///     Take one received fragment. Returns true and yields the reassembled body plus every message
    ///     in the set once the last fragment lands; returns false while the set is still incomplete.
    /// </summary>
    public bool TryAccept(Message message, SqsFragmentHeader header, out string body, out Message[] messages)
    {
        body = string.Empty;
        messages = [];

        lock (_locker)
        {
            expireStale();

            if (!_partials.TryGetValue(header.FragmentId, out var partial))
            {
                partial = new PartialMessage(header.Count, DateTimeOffset.UtcNow);
                _partials[header.FragmentId] = partial;
            }

            // A redelivery of a fragment already held is not an error - SQS standard queues are
            // at-least-once, so the same fragment arriving twice is expected rather than exceptional.
            partial.Bodies[header.Index] = message.Body;
            partial.Messages[header.Index] = message;

            if (partial.Bodies.Any(x => x is null))
            {
                return false;
            }

            _partials.Remove(header.FragmentId);

            body = string.Concat(partial.Bodies);
            messages = partial.Messages!;
            return true;
        }
    }

    /// <summary>
    ///     Drop fragment sets that have waited past the timeout without completing.
    /// </summary>
    /// <remarks>
    ///     Dropping means forgetting them here, NOT deleting them from SQS — they were never deleted, so
    ///     they simply become visible again and another attempt can be made. Without this the buffer
    ///     would grow without bound on a queue where fragments genuinely never converge.
    ///     </para>
    ///     <para>
    ///     Swept when the next fragment arrives rather than on a timer. A listener that has stopped
    ///     receiving fragments altogether holds its last partial set until one does, which is bounded by
    ///     <see cref="SqsMessageFragments.MaximumFragments" /> bodies and not worth a timer to reclaim.
    /// </remarks>
    private void expireStale()
    {
        if (_partials.Count == 0) return;

        var cutoff = DateTimeOffset.UtcNow.Subtract(_timeout);
        var stale = _partials.Where(x => x.Value.StartedAt < cutoff).Select(x => x.Key).ToArray();

        foreach (var id in stale)
        {
            var partial = _partials[id];
            _partials.Remove(id);

            _logger.LogWarning(
                "Abandoning an incomplete SQS message fragment set {FragmentId} after {Timeout} - held {Held} of {Count} fragments. " +
                "The fragments were never deleted from SQS, so they will become visible again. This usually means several nodes are " +
                "listening to one standard queue and no single one receives the whole set; see FragmentOversizedMessages().",
                id, _timeout, partial.Bodies.Count(x => x is not null), partial.Count);
        }
    }

    internal int PartialCount
    {
        get
        {
            lock (_locker)
            {
                return _partials.Count;
            }
        }
    }

    private class PartialMessage
    {
        public PartialMessage(int count, DateTimeOffset startedAt)
        {
            Count = count;
            StartedAt = startedAt;
            Bodies = new string?[count];
            Messages = new Message[count];
        }

        public int Count { get; }
        public DateTimeOffset StartedAt { get; }
        public string?[] Bodies { get; }
        public Message[] Messages { get; }
    }
}
