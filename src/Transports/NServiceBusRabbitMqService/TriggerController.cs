using Microsoft.AspNetCore.Mvc;
using NServiceBus;

namespace NServiceBusRabbitMqService;

public class TriggerController : ControllerBase
{
    [HttpPost("/trigger/{id}")]
    public Task Trigger(Guid id, [FromServices] IMessageSession sender)
    {
        // needs to be sent to "wolverine"
        return sender.Publish(new ResponseMessage { Id = id });
    }

    [HttpPost("/roundtrip/{id}")]
    public Task RoundTrip(Guid id, [FromServices] IMessageSession sender)
    {
        return sender.Publish(new ToWolverine { Id = id });
    }
    
    [HttpPost("/interface/{id}")]
    public Task Interface(Guid id, [FromServices] IMessageSession sender)
    {
        // needs to be sent to "wolverine"
        return sender.Publish<IInterfaceMessage>(x => x.Id = id);
    }
}