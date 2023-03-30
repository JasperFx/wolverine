using Wolverine.Runtime;

namespace Wolverine;

/// <summary>
/// Group of related, outgoing messages to use as a cascading message
/// mechanism in Wolverine message handlers
/// </summary>
public class OutgoingMessages : List<object>
{
    public void RespondToSender(object response)
    {
        Add(Respond.ToSender(response));
    }

    public void Schedule(object message, TimeSpan delay)
    {
        Add(message, new DeliveryOptions{ScheduleDelay = delay});
    }

    public void Schedule(object message, DateTimeOffset time)
    {
        Add(message, new DeliveryOptions{ScheduledTime = time});
    }

    public void Add(object message, DeliveryOptions options)
    {
        Add(new ConfiguredMessage(message, options));
    }
}