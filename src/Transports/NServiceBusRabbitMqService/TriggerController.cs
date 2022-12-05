using Microsoft.AspNetCore.Mvc;
using NServiceBus;

namespace NServiceBusRabbitMqService;

public class TriggerController : ControllerBase
{
    [HttpPost("/trigger/{id}")]
    public async Task Trigger(Guid id, [FromServices] IMessageSession sender)
    {
        // needs to be sent to "wolverine"
        await sender.Publish(new ResponseMessage { Id = id });
    }

    [HttpPost("/roundtrip/{id}")]
    public async Task RoundTrip(Guid id, [FromServices] IMessageSession sender)
    {
        await sender.Publish(new ToWolverine { Id = id });
    }
}