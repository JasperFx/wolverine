using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace MessageRoutingTests;


public class MessageRoutingContext : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(configure).StartAsync();
    }

    public IHost theHost => _host;

    protected virtual void configure(WolverineOptions opts)
    {
        // Nothing
    }

    protected void assertExternalListenersAre(string uriList)
    {
        var uris = uriList.ReadLines();
        
        var runtime = _host.GetRuntime();
        
        var actual = runtime
            .Endpoints
            .ActiveListeners()
            .Where(x => x.Endpoint.Role == EndpointRole.Application)
            .OrderBy(x => x.Uri.ToString())
            .Select(x => x.Uri).ToArray();

        var expected = uris
            .Where(x => x.IsNotEmpty())
            .OrderBy(x => x)
            .Select(x => new Uri(x))
            .ToArray();
        
        actual.ShouldBe(expected);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    protected void assertRoutesAre<T>(params string[] uris) where T : new()
    {
        var runtime = _host.GetRuntime();
        var messageRouter = runtime.RoutingFor(typeof(T));
        var envelopes = messageRouter.RouteForPublish(new T(), null);

        var expected = uris.OrderBy(x => x).Select(x => new Uri(x)).ToArray();
        var actual = envelopes.Select(x => x.Destination).OrderBy(x => x.ToString()).ToArray();

        actual.ShouldBe(expected);
    }

    protected void assertNoRoutes<T>() where T : new()
    {
        var runtime = _host.GetRuntime();
        var envelopes = runtime.RoutingFor(typeof(T)).RouteForPublish(new T(), null);
        envelopes.Any().ShouldBeFalse();
    }

    #region sample_PreviewRouting_programmatically

    public static void PreviewRouting(IHost host)
    {
        // In test projects, you would probably have access to the IHost for 
        // the running application

        // First, get access to the Wolverine runtime for the application
        // It's registered by Wolverine as a singleton in your IoC container
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        
        var router = runtime.RoutingFor(typeof(MyMessage));
        
        // If using Wolverine 3.6 or later when we added more
        // ToString() behavior for exactly this reason
        foreach (var messageRoute in router.Routes)
        {
            Debug.WriteLine(messageRoute);
        }
        
        // Otherwise, you might have to do this to "see" where
        // the routing is going
        foreach (var route in router.Routes.OfType<MessageRoute>())
        {
            Debug.WriteLine(route.Sender.Destination);
        }
    }

    #endregion
}

public record MyMessage;

public record M1;
public record M2;
public record M3;
public record M4;
public record M5;
public record NotLocallyHandled6;
public record NotLocallyHandled7;
public record NotLocallyHandled8;
public record NotLocallyHandled9;

public static class MHandler
{
    public static void Handle(M1 m) => Debug.WriteLine("Got M1");
    public static void Handle(M3 m) => Debug.WriteLine("Got M3");
    public static void Handle(M4 m) => Debug.WriteLine("Got M4");
    public static void Handle(M5 m) => Debug.WriteLine("Got M5");
}

[StickyHandler("blue")]
public static class BlueM5Handler
{
    public static void Handle(M5 m) => Debug.WriteLine("Got blue M5");
}

[StickyHandler("green")]
public static class GreenM5Handler
{
    public static void Handle(M5 m) => Debug.WriteLine("Got green M5");
}

public static class MainM2Handler
{
    public static void Handle(M2 m) => Debug.WriteLine("Got main  M2");
}

public static class OtherM2Handler
{
    public static void Handle(M2 m) => Debug.WriteLine("Got other M2");
}

public static class AnotherM2Handler
{
    public static void Handle(M2 m) => Debug.WriteLine("Got another M2");
}

public record OnlyStickyMessage;

public static class RedOnlyStickMessageHandler
{
    [StickyHandler("red")]
    public static void Handle(OnlyStickyMessage message) => Debug.WriteLine("Got red sticky message");
}

public static class PurpleOnlyStickMessageHandler
{
    [StickyHandler("purple")]
    public static void Handle(OnlyStickyMessage message) => Debug.WriteLine("Got purple sticky message");
}

public record ColorMessage;

[StickyHandler("red")]
public static class RedColorMessageHandler
{
    public static ColorMessage? LastMessage { get; set; }
    public static void Handle(ColorMessage m) => LastMessage = m;
}

public static class GreenColorMessageHandler
{
    public static ColorMessage? LastMessage { get; set; }
    public static void Handle(ColorMessage m) => LastMessage = m;
}

[StickyHandler("blue")]
public static class BlueColorMessageHandler
{
    public static ColorMessage? LastMessage { get; set; }
    public static void Handle(ColorMessage m) => LastMessage = m;
}

