using JasperFx.Core;
using Microsoft.AspNetCore.Mvc;
using OtelMessages;
using Wolverine;

namespace IntegrationTests;

public class MessageController : ControllerBase
{
    [HttpPost("/invoke")]
    public async Task Invoke([FromBody] InitialPost body, [FromServices] ICommandBus bus)
    {
        await Task.Delay(50.Milliseconds());
        await bus.InvokeAsync(new InitialCommand(body.Name));
    }

    [HttpPost("/enqueue")]
    public async Task Enqueue([FromBody] InitialPost body, [FromServices] ICommandBus bus)
    {
        await Task.Delay(50.Milliseconds());
        await bus.EnqueueAsync(new InitialCommand(body.Name));
    }
}