using Baseline.Dates;
using Wolverine;
using TestMessages;

namespace DocumentationSamples;

public class CustomizingMessageDelivery
{
    #region sample_SendMessagesWithDeliveryOptions

    public static async Task SendMessagesWithDeliveryOptions(IMessagePublisher publisher)
    {
        await publisher.PublishAsync(new Message1(), new DeliveryOptions
        {
            AckRequested = true,
            ContentType = "text/xml", // you can do this, but I'm not sure why you'd want to override this
            DeliverBy = DateTimeOffset.Now.AddHours(1), // set a message expiration date
            DeliverWithin = 1.Hours(), // convenience method to set the deliver-by expiration date
            ScheduleDelay = 1.Hours(), // Send this in one hour, or...
            ScheduledTime = DateTimeOffset.Now.AddHours(1),
            ResponseType = typeof(Message2) // ask the receiver to send this message back to you if it can
        }
            // There's a chained fluent interface for adding header values too
            .WithHeader("tenant", "one"));

    }

    #endregion
}
