using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.SignalR.Client;
using Wolverine.Util;

namespace Wolverine.SignalR.Tests;

public abstract class WebSocketTestContext : IAsyncLifetime
{
    protected WebApplication theWebApp;
    private readonly int Port = PortFinder.GetAvailablePort();
    protected readonly Uri clientUri;

    private readonly List<IHost> _clientHosts = new();

    public WebSocketTestContext()
    {
        clientUri = new Uri($"http://localhost:{Port}/messages");
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ListenLocalhost(Port);
        });
        
        builder.Services.AddSignalR();
        builder.Host.UseWolverine(opts =>
        {
            opts.ServiceName = "Server";
            
            opts.UseSignalR();

            opts.PublishMessage<FromFirst>().ToSignalR();
            opts.PublishMessage<FromSecond>().ToSignalR();
            opts.PublishMessage<Information>().ToSignalR();
        });

        var app = builder.Build();
        
        app.MapWolverineSignalRHub();
        
        await app.StartAsync();

        theWebApp = app;
    }

    public async Task<IHost> StartClientHost(string serviceName = "Client")
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                
                opts.UseSignalRClient($"http://localhost:{Port}/messages");
                
                opts.PublishMessage<ToFirst>().ToSignalRWithClient(Port, "/messages");
                
                opts.PublishMessage<RequiresResponse>().ToSignalRWithClient(Port, "/messages");
                
                opts.Publish(x =>
                {
                    x.MessagesImplementing<WebSocketMessage>();
                    x.ToSignalRWithClient(Port, "/messages");
                });
            }).StartAsync();
        
        _clientHosts.Add(host);

        return host;
    }

    public async Task DisposeAsync()
    {
        await theWebApp.StopAsync();

        foreach (var clientHost in _clientHosts)
        {
            await clientHost.StopAsync();
        }
    }
    
}

public record ToFirst(string Name) : WebSocketMessage;
public record FromFirst(string Name) : WebSocketMessage;
public record ToSecond(string Name) : WebSocketMessage;
public record FromSecond(string Name) : WebSocketMessage;

public static class WebSocketMessageHandler
{
    public static void Handle(ToFirst m) => Debug.WriteLine("Got " + m);
    public static void Handle(FromFirst m) => Debug.WriteLine("Got " + m);
    public static void Handle(ToSecond m) => Debug.WriteLine("Got " + m);
    public static void Handle(FromSecond m) => Debug.WriteLine("Got " + m);
}

