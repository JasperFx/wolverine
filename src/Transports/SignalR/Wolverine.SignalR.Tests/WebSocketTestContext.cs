using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.SignalR.Client;
using Wolverine.Util;

namespace Wolverine.SignalR.Tests;

#region sample_signalr_client_test_harness_setup

public abstract class WebSocketTestContext : IAsyncLifetime
{
    protected WebApplication theWebApp;
    protected readonly int Port = PortFinder.GetAvailablePort();
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

        #endregion

        builder.Services.AddSignalR();
        builder.Host.UseWolverine(opts =>
        {
            opts.ServiceName = "Server";

            // Hooking up the SignalR messaging transport
            // in Wolverine
            opts.UseSignalR();

            // These are just some messages I was using
            // to do end to end testing
            opts.PublishMessage<FromFirst>().ToSignalR();
            opts.PublishMessage<FromSecond>().ToSignalR();
            opts.PublishMessage<Information>().ToSignalR();

            opts.PublishMessage<MathAnswer>().ToSignalR();
        });

        var app = builder.Build();

        // Syntactic sure, really just doing:
        // app.MapHub<WolverineHub>("/messages");
        app.MapWolverineSignalRHub();

        await app.StartAsync();

        // Remember this, because I'm going to use it in test code
        // later
        theWebApp = app;
    }

    // This starts up a new host to act as a client to the SignalR
    // server for testing
    public async Task<IHost> StartClientHost(string serviceName = "Client")
    {
        #region sample_bootstrapping_signalr_client_in_test

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;

                opts.UseClientToSignalR(Port);

                opts.PublishMessage<ToFirst>().ToSignalRWithClient(Port);

                opts.PublishMessage<RequiresResponse>().ToSignalRWithClient(Port);

                opts.Publish(x =>
                {
                    x.MessagesImplementing<WebSocketMessage>();
                    x.ToSignalRWithClient(Port);
                });
            }).StartAsync();

        #endregion

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

public abstract class WebSocketTestContextWithCustomHub<THub> : IAsyncLifetime where THub : WolverineHub
{
    protected WebApplication theWebApp;
    protected readonly int Port = PortFinder.GetAvailablePort();
    protected readonly Uri clientUri;

    private readonly List<IHost> _clientHosts = new();

    public WebSocketTestContextWithCustomHub()
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

        #region sample_custom_signalr_hub
        builder.Services.AddSignalR();
        builder.Host.UseWolverine(opts =>
        {
            opts.ServiceName = "Server";

            // Hooking up the SignalR messaging transport
            // in Wolverine using a custom hub
            opts.UseSignalR<THub>();

            // A message for testing
            opts.PublishMessage<FromSecond>().ToSignalR();
        });

        var app = builder.Build();

        // Syntactic sugar, really just doing:
        // app.MapHub<THub>("/messages");
        app.MapWolverineSignalRHub<THub>();
        #endregion

        await app.StartAsync();

        // Remember this, because I'm going to use it in test code
        // later
        theWebApp = app;
    }

    // This starts up a new host to act as a client to the SignalR
    // server for testing
    public async Task<IHost> StartClientHost(string serviceName = "Client")
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;

                opts.UseClientToSignalR(Port);

                opts.PublishMessage<ToFirst>().ToSignalRWithClient(Port);

                opts.PublishMessage<RequiresResponse>().ToSignalRWithClient(Port);

                opts.Publish(x =>
                {
                    x.MessagesImplementing<WebSocketMessage>();
                    x.ToSignalRWithClient(Port);
                });
            }).StartAsync();

        _clientHosts.Add(host);

        return host;
    }

    public virtual async Task DisposeAsync()
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

