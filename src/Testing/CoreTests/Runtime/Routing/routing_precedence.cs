using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Runtime.Routing;

public class routing_precedence
{
    [Fact]
    public async Task local_routing_is_applied_automatically()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        bus.PreviewSubscriptions(new BlueMessage())
            .Single().Destination.ShouldBe(new Uri("local://blue"));
    }
    
    
    [Fact]
    public async Task respect_local_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        bus.PreviewSubscriptions(new GreenMessage())
            .Single().Destination.ShouldBe(new Uri("local://seagreen"));
        
        bus.PreviewSubscriptions(new DarkGreenMessage())
            .Single().Destination.ShouldBe(new Uri("local://seagreen"));
    }
        
    [Fact]
    public async Task explicit_routing_to_local_wins()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<BlueMessage>().ToLocalQueue("purple");
            }).StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        bus.PreviewSubscriptions(new BlueMessage())
            .Single().Destination.ShouldBe(new Uri("local://purple"));
    }
    
    [Fact]
    public async Task explicit_routing_to_elsewhere_wins()
    {
        var port = PortFinder.GetAvailablePort();
        
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<BlueMessage>().ToPort(port);
            }).StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        bus.PreviewSubscriptions(new BlueMessage())
            .Single().Destination.ShouldBe(new Uri("tcp://localhost:" + port));
    }

    [Fact]
    public async Task local_takes_precedence_on_other_routers()
    {
        var port = PortFinder.GetAvailablePort();
        var convention = new FakeRoutingConvention
        {
            Senders =
            {
                [typeof(BlueMessage)] = port
            }
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.RouteWith(convention);
            }).StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        bus.PreviewSubscriptions(new BlueMessage())
            .Single().Destination.ShouldBe(new Uri("local://blue"));
    }

    [Fact]
    public async Task fall_through_to_other_rules_if_no_local()
    {
        var port = PortFinder.GetAvailablePort();
        var convention = new FakeRoutingConvention
        {
            Senders =
            {
                [typeof(RedMessage)] = port
            }
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.RouteWith(convention);
            }).StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        bus.PreviewSubscriptions(new RedMessage())
            .Single().Destination.ShouldBe(new Uri("tcp://localhost:" + port));
    }

    [Fact]
    public async Task use_local_invoker_if_local_exists()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var collection = host.Services.GetRequiredService<IWolverineRuntime>();
        var local = collection.FindInvoker(typeof(BlueMessage)).ShouldBeOfType<Wolverine.Runtime.Handlers.Executor>();
        
        collection.FindInvoker(typeof(BlueMessage))
            .ShouldBeSameAs(local);
    }
    
    
    [Fact]
    public async Task favor_local_invoker_if_local_exists()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<RedMessage>().ToPort(port);
            }).StartAsync();

        var collection = host.Services.GetRequiredService<IWolverineRuntime>();
        var local = collection.FindInvoker(typeof(BlueMessage)).ShouldBeOfType<Wolverine.Runtime.Handlers.Executor>();
        
        collection.FindInvoker(typeof(BlueMessage))
            .ShouldBeSameAs(local);
    }
    
    [Fact]
    public async Task use_messageroute_if_cannot_handle_and_subscriber_exists()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<RedMessage>().ToPort(port);
            }).StartAsync();

        var collection = host.Services.GetRequiredService<IWolverineRuntime>();
        var remote = collection.FindInvoker(typeof(RedMessage)).ShouldBeOfType<MessageRoute>();
        
        collection.FindInvoker(typeof(RedMessage))
            .ShouldBeSameAs(remote);
    }

    [Fact]
    public async Task use_no_handler_if_no_handler_and_no_subscriber()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {

            }).StartAsync();
        
        var collection = host.Services.GetRequiredService<IWolverineRuntime>();
        collection.FindInvoker(typeof(RedMessage))
            .ShouldBeOfType<NoHandlerExecutor>();
    }
}

public class ColorsMessageHandler
{
    public void Handle(BlueMessage message){}
    public void Handle(GreenMessage message){}
    public void Handle(DarkGreenMessage message){}
}

[MessageIdentity("blue")]
public record BlueMessage;
public record RedMessage;

[LocalQueue("seagreen")]
public record GreenMessage;

[LocalQueue("seagreen")]
public record DarkGreenMessage;

public class FakeRoutingConvention : IMessageRoutingConvention
{
    public Dictionary<Type, int> Senders { get; } = new();
    

    public void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes)
    {
        // Nothing
    }

    public IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        if (Senders.TryGetValue(messageType, out var port))
        {
            var endpoint = runtime.Endpoints.GetOrBuildSendingAgent(new Uri("tcp://localhost:" + port)).Endpoint;
            yield return endpoint;
        }
    }
}