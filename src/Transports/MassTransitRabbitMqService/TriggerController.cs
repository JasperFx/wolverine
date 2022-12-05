using System;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace MassTransitService;

public class TriggerController : ControllerBase
{
    [HttpPost("/trigger/{id}")]
    public async Task Trigger(Guid id, [FromServices] ISendEndpointProvider sender)
    {
        var endpoint = await sender.GetSendEndpoint(new Uri("rabbitmq://localhost/wolverine"));
        await endpoint.Send(new ResponseMessage { Id = id });
    }

    [HttpPost("/roundtrip/{id}")]
    public async Task RoundTrip(Guid id, [FromServices] ISendEndpointProvider sender)
    {
        var endpoint = await sender.GetSendEndpoint(new Uri("rabbitmq://localhost/wolverine"));
        await endpoint.Send(new ToWolverine { Id = id });
    }
}