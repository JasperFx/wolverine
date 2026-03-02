namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible options for sending a command message.
/// Maps internally to Wolverine's <see cref="DeliveryOptions"/>.
/// </summary>
public class SendOptions : ExtendableOptions
{
    /// <summary>
    /// Sets the destination endpoint for this send operation.
    /// Maps to Wolverine's <see cref="IMessageBus.EndpointFor(string)"/>.
    /// </summary>
    public string? Destination { get; set; }

    /// <summary>
    /// Sets the destination endpoint for the send operation.
    /// </summary>
    public void SetDestination(string destination)
    {
        Destination = destination;
    }

    /// <summary>
    /// Delays the delivery of the message by the specified time span.
    /// Maps to <see cref="DeliveryOptions.ScheduleDelay"/>.
    /// </summary>
    public void DelayDeliveryWith(TimeSpan delay)
    {
        Delay = delay;
        ScheduledTime = null;
    }

    /// <summary>
    /// Delays the delivery of the message until the specified time.
    /// Maps to <see cref="DeliveryOptions.ScheduledTime"/>.
    /// </summary>
    public void DoNotDeliverBefore(DateTimeOffset at)
    {
        ScheduledTime = at;
        Delay = null;
    }

    internal TimeSpan? Delay { get; set; }
    internal DateTimeOffset? ScheduledTime { get; set; }

    internal override DeliveryOptions ToDeliveryOptions()
    {
        var options = base.ToDeliveryOptions();

        if (Delay.HasValue)
        {
            options.ScheduleDelay = Delay;
        }

        if (ScheduledTime.HasValue)
        {
            options.ScheduledTime = ScheduledTime;
        }

        return options;
    }
}
