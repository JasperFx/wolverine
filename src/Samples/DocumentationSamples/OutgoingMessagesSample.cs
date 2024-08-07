using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;

namespace DocumentationSamples;

public static class OutgoingMessageHandler
{
    #region sample_using_OutgoingMessage

    public static OutgoingMessages Handle(Incoming incoming)
    {
        // You can use collection initializers for OutgoingMessages in C#
        // as a shorthand.
        var messages = new OutgoingMessages
        {
            new Message1(),
            new Message2(),
            new Message3(),
        };

        // Send a specific message back to the original sender
        // of the incoming message
        messages.RespondToSender(new Message4());

        // Send a message with a 5 minute delay
        messages.Delay(new Message5(), 5.Minutes());

        // Schedule a message to be sent at a specific time
        messages.Schedule(new Message5(), new DateTimeOffset(2023, 4, 5, 0, 0, 0, 0.Minutes()));

        return messages;
    }

    #endregion

    #region sample_customized_cascaded_messages

    public static IEnumerable<object> Consume(Incoming incoming)
    {
        // Delay the message delivery by 10 minutes
        yield return new Message1().DelayedFor(10.Minutes());

        // Schedule the message delivery for a certain time
        yield return new Message2().ScheduledAt(new DateTimeOffset(DateTime.Today.AddDays(2)));

        // Customize the message delivery however you please...
        yield return new Message3()
            .WithDeliveryOptions(new DeliveryOptions().WithHeader("foo", "bar"));

        // Send back to the original sender
        yield return Respond.ToSender(new Message4());
    }

    #endregion
}

public record Incoming;