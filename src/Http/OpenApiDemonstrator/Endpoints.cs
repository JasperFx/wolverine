using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Swashbuckle.AspNetCore.Swagger;
using Wolverine.Http;

namespace OpenApiDemonstrator;

public static class Endpoints
{
    [WolverineGet("/json")]
    public static ResponseModel GetReservation()
    {
        return new ResponseModel();
    }

    [WolverinePost("/message"), EmptyResponse]
    public static Message1 PostMessage()
    {
        return new Message1();
    }
}

public class BuildSwagger : IHostedService
{
    private readonly ISwaggerProvider _provider;

    public BuildSwagger(ISwaggerProvider provider)
    {
        _provider = provider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        //var document = _provider.GetSwagger("v1");

        //Debug.WriteLine(document);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class Message1;

public class ResponseModel
{
    public string Name { get; set; }
    public int Age { get; set; }
}