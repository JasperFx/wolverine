using System.Diagnostics;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Module2;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Routing;

public class routing_rules
{
    [Fact]
    public async Task local_routing_is_applied_automatically()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var bus = host.MessageBus();
        bus.PreviewSubscriptions(new BlueMessage())
            .Single().Destination.ShouldBe(new Uri("local://blue"));
    }

    [Fact]
    public async Task create_descriptor()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var runtime = host.GetRuntime();

        var routes = runtime.RoutingFor(typeof(Message1)).Routes;
        var descriptor = routes.Single().Describe();
        descriptor.Endpoint.ShouldBe(new Uri("local://message1"));
        descriptor.ContentType.ShouldBe("application/json");

    }

    [Fact]
    public async Task can_disable_local_routing_convention()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.DisableConventionalLocalRouting();
            }).StartAsync();

        var bus = host.MessageBus();
        bus.PreviewSubscriptions(new BlueMessage())
            .Any().ShouldBeFalse();
    }

    [Fact]
    public async Task respect_local_queue()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var bus = host.MessageBus();
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

        var bus = host.MessageBus();
        bus.PreviewSubscriptions(new BlueMessage())
            .Single().Destination.ShouldBe(new Uri("local://purple"));
    }

    #region sample_using_preview_subscriptions

    public static void using_preview_subscriptions(IMessageBus bus)
    {
        // Preview where Wolverine is wanting to send a message
        var outgoing = bus.PreviewSubscriptions(new BlueMessage());
        foreach (var envelope in outgoing)
        {
            // The URI value here will identify the endpoint where the message is
            // going to be sent (Rabbit MQ exchange, Azure Service Bus topic, Kafka topic, local queue, etc.)
            Debug.WriteLine(envelope.Destination);
        }
    }

    #endregion

    [Fact]
    public async Task explicit_routing_to_elsewhere_wins()
    {
        var port = PortFinder.GetAvailablePort();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishMessage<BlueMessage>().ToPort(port);
            }).StartAsync();

        var bus = host.MessageBus();
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

        var bus = host.MessageBus();
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

        var bus = host.MessageBus();
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

    // This one is for https://github.com/JasperFx/wolverine/issues/1099
    [Fact]
    public async Task route_with_fluent_interface()
    {
        var port = PortFinder.GetAvailablePort();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                //opts.Discovery.IncludeAssembly(typeof(Module2Message1).Assembly);
                opts.Publish().MessagesFromAssembly(typeof(Module2Message1).Assembly).ToPort(port);

            }).StartAsync();
        
        var bus = host.MessageBus();
        var envelopes = bus.PreviewSubscriptions(new Module2Message1());
        
        envelopes.Single().Destination.ShouldBe(new Uri("tcp://localhost:" + port));
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
        if (_onlyApplyToOutboundMessages)
        {
            return;
        }

        // Nothing
    }

    public IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime)
    {
        if (_onlyApplyToInboundMessages)
        {
            yield break;
        }

        if (Senders.TryGetValue(messageType, out var port))
        {
            var endpoint = runtime.Endpoints.GetOrBuildSendingAgent(new Uri("tcp://localhost:" + port)).Endpoint;
            yield return endpoint;
        }
    }

    private bool _onlyApplyToOutboundMessages;

    public void OnlyApplyToOutboundMessages()
    {
        _onlyApplyToInboundMessages = false;
        _onlyApplyToOutboundMessages = true;
    }

    private bool _onlyApplyToInboundMessages;

    public void OnlyApplyToInboundMessages()
    {
        _onlyApplyToOutboundMessages = false;
        _onlyApplyToInboundMessages = true;
    }
}