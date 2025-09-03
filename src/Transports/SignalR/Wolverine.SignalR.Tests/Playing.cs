using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.SignalR.Tests;

public class Playing
{
    public static async Task Do(WolverineHub hub)
    {
        await hub.Clients.All.SendAsync("method", "json");

        var services = new ServiceCollection();
        services.AddSignalR();
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapHub<WolverineHub>("/wolverine");
    }
}

public class WolverineHub : Hub
{
    private readonly IWolverineRuntime _runtime;

    public WolverineHub(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task StartAsync()
    {
    }
    
    public override Task OnConnectedAsync()
    {
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        return base.OnDisconnectedAsync(exception);
    }
    
    public async Task SendMessage(string user, string message)
    {
        await Clients.All.SendAsync("ReceiveMessage", user, message);
    }
}