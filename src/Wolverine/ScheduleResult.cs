namespace Wolverine;

/// <summary>
///     Result of scheduling a message via <see cref="MessageBusExtensions.ScheduleWithResultAsync{T}(IMessageBus, T, DateTimeOffset, DeliveryOptions?)"/>.
///     Contains the envelope(s) created by the scheduling operation, each with a transport-populated
///     <see cref="Envelope.SchedulingToken"/> when the transport supports it.
/// </summary>
public class ScheduleResult
{
    public ScheduleResult(IReadOnlyList<Envelope> envelopes)
    {
        Envelopes = envelopes;
    }

    /// <summary>
    ///     The envelopes created by the scheduling operation. Each envelope's
    ///     <see cref="Envelope.SchedulingToken"/> will be populated if the transport supports it.
    /// </summary>
    public IReadOnlyList<Envelope> Envelopes { get; }
}
