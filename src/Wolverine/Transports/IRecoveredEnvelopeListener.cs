namespace Wolverine.Transports;

/// <summary>
/// The listener a receiver is handed along with envelopes that came out of the message store (inbox recovery, a
/// scheduled retry firing) rather than from a broker. On 6.x it only tells the inline and native ack receivers that
/// the envelope is not a broker delivery, so their in-memory idempotency guard does not treat an inbox retry as a
/// duplicate of the delivery it replaced (#5980). Main also uses it for GH-5676's buffered receiver settlement.
/// </summary>
internal interface IRecoveredEnvelopeListener : IListener;
