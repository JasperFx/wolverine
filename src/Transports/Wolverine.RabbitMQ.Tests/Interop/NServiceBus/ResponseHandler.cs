using System.Collections.Generic;
using System.Threading.Tasks;
using NServiceBusRabbitMqService;

namespace Wolverine.RabbitMQ.Tests.Interop.NServiceBus;

public class ResponseHandler
{
    public static IList<Envelope> Received = new List<Envelope>();

    public static ValueTask Handle(ResponseMessage message, Envelope envelope, IMessageContext context)
    {
        Received.Add(envelope);

        return context.RespondToSenderAsync(new ToExternal { Id = message.Id });
    }
    
    public static ValueTask Handle(ConcreteMessage message, Envelope envelope, IMessageContext context)
    {
        Received.Add(envelope);

        return context.RespondToSenderAsync(new ToExternal { Id = message.Id });
    }
}

public class ConcreteMessage : IInterfaceMessage
{
    public Guid Id { get; set; }
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