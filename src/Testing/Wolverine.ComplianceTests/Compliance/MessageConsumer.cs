using System.Diagnostics;
using Wolverine;
using Wolverine.Attributes;

namespace Wolverine.ComplianceTests.Compliance;

public class MessageConsumer
{
    public void Consume(Message1 message, Envelope envelope)
    {
        Debug.WriteLine("Hey");
    }

    [RequeueOn(typeof(DivideByZeroException))]
    public void Consume(Message2 message, Envelope envelope)
    {
        if (envelope.Attempts < 2)
        {
            throw new DivideByZeroException();
        }
    }

    public void Consume(TimeoutsMessage message, Envelope envelope)
    {
        if (envelope.Attempts < 2)
        {
            throw new TimeoutException();
        }
    }

    public Response Handle(Request request)
    {
        return new Response { Name = request.Name };
    }
}

public class Request
{
    public string Name { get; set; }
}

public class Response
{
    public string Name { get; set; }
}