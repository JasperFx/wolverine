using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Wolverine;
using Wolverine.Http;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddAuthorization();
builder.Services.AddWolverine(c =>
{
    //c.ApplicationAssembly = typeof(Endpoint1).Assembly;
    c.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
});
builder.Services.AddWolverineHttp();
builder.Services.AddHostedService<ClientHostedService>();

var app = builder.Build();

app.MapWolverineEndpoints();
    
return await app.RunJasperFxCommands(args);

public static class Endpoint1
{
    [WolverineGet("/api/e1")]
    public static string[] Get()
    {
        return ["Foo"];
    }
}

public static class Endpoint2
{
    [WolverineGet("/api/e2")]
    public static string[] Get()
    {
        return ["Bar"];
    }
}

internal class Client(string baseAddress)
{
    private HttpClient _client = new();

    public async Task<bool> CallEndpointsConcurrently()
    {
        try
        {
            await Task.WhenAll(
                _client.GetStringAsync($"{baseAddress}/api/e1"),
                _client.GetStringAsync($"{baseAddress}/api/e2")
            );

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed! {ex}");
            return false;
        }

        return true;
    }
}

// boring infra.
internal class ClientHostedService(IServer server, IHostApplicationLifetime hostApplicationLifetime) : IHostedService
{

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hostApplicationLifetime.ApplicationStarted.Register((_, ct) =>
        {
            StartClientInLoop(cancellationToken);
        }, null);
        return Task.CompletedTask;
    }

    private async Task StartClientInLoop(CancellationToken token)
    {
        await Task.Delay(2000, token);

        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.First();
        var client = new Client(address);
        while (!token.IsCancellationRequested)
        {
            var success = await client.CallEndpointsConcurrently();
            if (!success)
            {
                break;
            }
            await Task.Delay(50, token);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}



