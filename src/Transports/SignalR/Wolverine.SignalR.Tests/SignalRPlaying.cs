using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.SignalR.Tests;

public class SignalRPlaying
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