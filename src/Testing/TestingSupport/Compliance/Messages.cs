using Wolverine;
using Wolverine.Attributes;

namespace TestingSupport.Compliance;

public class NewUser
{
    public string UserId { get; set; }
}

public class EditUser;

public class DeleteUser
{
    public int Number1;
    public int Number2;
    public int Number3;
}

#region sample_PingAndPongMessage

public class PingMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
}

public class PongMessage
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

#endregion

public class ImplicitPing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
}

public class ImplicitPong
{
    public Guid Id { get; set; }
    public string Name { get; set; }
}

public class PingHandler
{
    #region sample_respond_to_sender

    public object Handle(PingMessage message)
    {
        var pong = new PongMessage { Id = message.Id };

        // This will send the pong message back
        // to the original sender of the PingMessage
        return Respond.ToSender(pong);
    }

    #endregion

    public ImplicitPong Handle(ImplicitPing ping)
    {
        return new ImplicitPong
        {
            Id = ping.Id
        };
    }
}

public class PongHandler
{
    public void Handle(PongMessage message)
    {
    }

    public void Handle(ImplicitPong message)
    {
    }
}

public class UserCreated
{
    public Guid Id { get; set; }
    public string UserId { get; set; }
}

public class UserDeleted
{
    public Guid Id { get; set; }
    public string UserId { get; set; }
}

public class SentTrack
{
    public Guid Id { get; set; }
    public string MessageType { get; set; }
}

public class ReceivedTrack
{
    public Guid Id { get; set; }
    public string MessageType { get; set; }
}

public class TimeoutsMessage;

public class ExecutedMessage;

public class ExecutedMessageGuy
{
    public static void Handle(ExecutedMessage message)
    {
    }
}

public class ColorHandler
{
    public void Handle(ColorChosen message, ColorHistory history, Envelope envelope)
    {
        history.Name = message.Name;
        history.Envelope = envelope;
    }
}

public class ColorHistory
{
    public string Name { get; set; }
    public Envelope Envelope { get; set; }
}

public class ColorChosen
{
    public string Name { get; set; }
}

public class TracksMessage<T>
{
    public void Handle(T message)
    {
    }
}

[MessageIdentity("A")]
public class TopicA
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

[MessageIdentity("B")]
public class TopicB
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

[MessageIdentity("C")]
public class TopicC
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class SpecialTopic
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

[MessageIdentity("Structural.Typed.Message")]
public class BlueMessage
{
    public string Name { get; set; }
}

[MessageIdentity("Structural.Typed.Message")]
public class GreenMessage
{
    public string Name { get; set; }
}