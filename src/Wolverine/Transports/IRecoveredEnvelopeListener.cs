namespace Wolverine.Transports;

/// <summary>
/// GH-5676. The listener a receiver is handed along with envelopes that came out of the message store (inbox
/// recovery, a dead letter replay, a scheduled message firing) rather than from a broker. Its
/// <see cref="IChannelCallback.CompleteAsync"/> marks the envelope's inbox row Handled. A buffered receiver must run
/// that as part of the envelope's own completion, after the handler succeeds, and not when the envelope arrives:
/// marking it on arrival loses the message if the node stops before the handler runs, and lets a tracked session
/// finish before the row is marked.
/// </summary>
internal interface IRecoveredEnvelopeListener : IListener;
