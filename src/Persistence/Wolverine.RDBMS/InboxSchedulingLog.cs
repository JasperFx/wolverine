using Microsoft.Extensions.Logging;

namespace Wolverine.RDBMS;

internal static class InboxSchedulingLog
{
    /// <summary>
    /// GH-5120. Rescheduling is an UPDATE of an existing inbox row, and one that matched nothing used to succeed
    /// silently: a batch carrier with no row of its own lost its retry that way and nobody could tell. Nothing
    /// here can know whether the message already completed (a Handled row is excluded on purpose, GH-4216) or was
    /// never persisted, so say so and leave it to whoever reads the log.
    /// </summary>
    public static void MatchedNothing(ILogger logger, Envelope envelope)
    {
        logger.LogWarning(
            "Scheduling envelope {EnvelopeId} ({MessageType}) for execution at {ScheduledTime} matched no inbox row at {Destination}. Either it already completed, or it was never persisted and this retry will not run",
            envelope.Id, envelope.MessageType, envelope.ScheduledTime, envelope.Destination);
    }
}
