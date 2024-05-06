using System.Diagnostics;
using Wolverine;
using Wolverine.Attributes;

namespace TestingSupport.Compliance;

[MessageIdentity("Message1")]
public class Message1
{
    public Message1()
    {
        Debug.WriteLine("Built.");
    }

    public Guid Id { get; set; } = Guid.NewGuid();
}

[MessageIdentity("Message2")]
public class Message2
{
    public Guid Id = Guid.NewGuid();
}

[MessageIdentity("Message3")]
public class Message3;

[MessageIdentity("Message4")]
public class Message4;

[MessageIdentity("Message5")]
public class Message5
{
    public int FailThisManyTimes = 0;
    public Guid Id = Guid.NewGuid();
}

public class InvokedMessage
{
    public int FailThisManyTimes = 0;
    public Guid Id = Guid.NewGuid();
}

[RetryNow(typeof(DivideByZeroException), 5, 10, 25)]
public class InvokedMessageHandler
{
    public void Handle(InvokedMessage message, Envelope envelope)
    {
        if (envelope.Attempts <= message.FailThisManyTimes)
        {
            throw new DivideByZeroException();
        }
    }
}