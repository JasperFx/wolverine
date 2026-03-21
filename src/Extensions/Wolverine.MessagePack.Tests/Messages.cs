using MessagePack;

namespace Wolverine.MessagePack.Tests;

[MessagePackObject]
public class MessagePackMessage
{
    [Key(0)]
    public string Name = null!;
}

[MessagePackObject(keyAsPropertyName: true)]
public class MessagePackKeylessMessage
{
    public string Name = null!;
}

[MessagePackObject]
public record MessagePackRecordMessage([property: Key(0)] string Name);


// fake handler
public class MessagePackMessageHandler
{
    public void Handle(MessagePackMessage message)
    {
    }

    public void Handle(MessagePackKeylessMessage message)
    {
    }

    public void Handle(MessagePackRecordMessage message)
    {
    }
}
