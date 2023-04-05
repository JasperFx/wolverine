using JasperFx.Core;
using TestMessages;
using Wolverine;

namespace DocumentationSamples;

public class OutgoingMessageHandler
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
}

public record Incoming;