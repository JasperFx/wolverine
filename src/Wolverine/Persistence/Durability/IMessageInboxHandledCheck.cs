namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-5049. Optional capability of an <see cref="IMessageInbox" /> that can tell a message it has finished with
/// (a retained <see cref="EnvelopeStatus.Handled" /> row) apart from one that is still waiting or running (an
/// <see cref="EnvelopeStatus.Incoming" /> row), which <see cref="IMessageInbox.ExistsAsync" /> cannot. The push
/// receiver's inbox deduplication claims a message id with an Incoming row before running the handler, and needs
/// this to decide whether a delivery that finds the id already there is a duplicate of a finished message, to ack,
/// or a concurrent delivery of one still in progress, which the broker has to redeliver later.
/// </summary>
/// <remarks>
/// A separate interface rather than a member of <see cref="IMessageInbox" />, so a third-party store needs no
/// change. Without it, the push receiver treats any existing row as handled, as it did before GH-5049.
/// </remarks>
public interface IMessageInboxHandledCheck
{
    /// <summary>
    /// Whether the inbox holds this envelope's identity as <see cref="EnvelopeStatus.Handled" />, by the same
    /// identity rule as <see cref="IMessageInbox.ExistsAsync" />
    /// </summary>
    Task<bool> IsHandledAsync(Envelope envelope, CancellationToken cancellation);
}

internal static class MessageInboxHandledCheckExtensions
{
    /// <summary>
    /// Whether the inbox holds the envelope as handled, or null when this inbox cannot tell
    /// </summary>
    public static async Task<bool?> TryFindHandledAsync(this IMessageInbox inbox, Envelope envelope,
        CancellationToken cancellation)
    {
        if (inbox is IMessageInboxHandledCheck check)
        {
            return await check.IsHandledAsync(envelope, cancellation).ConfigureAwait(false);
        }

        return null;
    }
}
