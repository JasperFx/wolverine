using System.Collections.Generic;
using System.Threading.Tasks;
using MassTransitService;

namespace Wolverine.RabbitMQ.Tests.Interop.MassTransit;

public class ResponseHandler
{
    public static IList<Envelope> Received = new List<Envelope>();

    public static ValueTask Handle(ResponseMessage message, Envelope envelope, IMessageContext context)
    {
        Received.Add(envelope);
        return context.RespondToSenderAsync(new ToExternal { Id = message.Id });
    }
}

public class ToWolverineHandler
{
    public static ValueTask Handle(ToWolverine message, IMessageContext context)
    {
        var response = new ToExternal
        {
            Id = message.Id
        };

        return context.RespondToSenderAsync(response);
    }
}