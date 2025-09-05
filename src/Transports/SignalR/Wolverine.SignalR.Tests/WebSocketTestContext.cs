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
    protected readonly Uri firstUri;
    protected readonly Uri secondUri;

    private readonly List<IHost> _clientHosts = new();

    public WebSocketTestContext()
    {
        firstUri = new Uri($"http://localhost:{Port}/first");
        secondUri = new Uri($"http://localhost:{Port}/second");
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
            
            opts.UseSignalR<FirstHub>();
            opts.UseSignalR<SecondHub>();

            opts.PublishMessage<FromFirst>().ToSignalR<FirstHub>();
            opts.PublishMessage<FromSecond>().ToSignalR<SecondHub>();
        });

        var app = builder.Build();
        app.MapHub<FirstHub>("/first");
        app.MapHub<SecondHub>("/second");
        
        await app.StartAsync();

        theWebApp = app;
    }

    public async Task<IHost> StartClientHost(string serviceName = "Client")
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                
                opts.UseSignalRClient($"http://localhost:{Port}/first");
                opts.UseSignalRClient($"http://localhost:{Port}/second");
                
                opts.PublishMessage<ToFirst>().ToSignalRWithClient(Port, "/first");
                opts.PublishMessage<ToSecond>().ToSignalRWithClient(Port, "/second");
                
                opts.PublishMessage<RequiresResponse>().ToSignalRWithClient(Port, "/first");
                
                opts.Publish(x =>
                {
                    x.MessagesImplementing<WebSocketMessage>();
                    x.ToSignalRWithClient(Port, "/second");
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


public class FirstHub : WolverineHub
{
    public FirstHub(IWolverineRuntime runtime) : base(runtime)
    {
    }
}

public class SecondHub : WolverineHub
{
    public SecondHub(IWolverineRuntime runtime) : base(runtime)
    {
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

