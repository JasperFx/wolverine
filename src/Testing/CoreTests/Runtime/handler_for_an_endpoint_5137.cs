using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5137. With one handler chain per handler type, what runs a message received at an endpoint is decided per
/// (message type, endpoint): the chain bound to that endpoint, else the message type's only chain, else a relay
/// to the local queues of the chains, else a clear error. At most one chain of a message type may be bound to any
/// one endpoint, and that is checked at startup.
/// </summary>
public class handler_for_an_endpoint_5137
{
    private static async Task<IHost> hostWith(Action<WolverineOptions> configure, params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);

                configure(opts);
            }).StartAsync();
    }

    private static Endpoint endpointNamed(IHost host, string name)
        => host.GetRuntime().Options.Transports.AllEndpoints().Single(x => x.EndpointName == name);

    private static Uri queueOf<THandler>() => new("local://" + typeof(THandler).FullNameInCode().ToLowerInvariant());

    [Fact]
    public async Task two_handler_types_sticky_to_the_same_endpoint_is_a_startup_error()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => hostWith(_ => { },
            typeof(FirstStickyToSameQueue5137), typeof(SecondStickyToSameQueue5137)));

        ex.Message.ShouldContain(typeof(FirstStickyToSameQueue5137).FullNameInCode());
        ex.Message.ShouldContain(typeof(SecondStickyToSameQueue5137).FullNameInCode());
        ex.Message.ShouldContain(typeof(CollidingMessage5137).FullNameInCode());
        ex.Message.ShouldContain("local://same-queue-5137");
    }

    [Fact]
    public async Task a_message_on_the_message_types_own_queue_is_relayed_to_every_handler_type()
    {
        // The message type's own local queue is where envelopes persisted before an upgrade split the message's
        // handler types are still addressed. Nothing listens there by convention any more, and those envelopes
        // have to drain to every handler type rather than fail
        using var host = await hostWith(_ => { }, typeof(FirstSplitHandler5137), typeof(SecondSplitHandler5137));

        SplitHandlers5137.Received.Clear();

        var oldQueue = new Uri("local://" + typeof(SplitMessage5137).ToMessageTypeName().ToLowerInvariant());
        var message = new SplitMessage5137(Guid.NewGuid());

        var session = await host.TrackActivity()
            .ExecuteAndWaitAsync(c => c.EndpointFor(oldQueue).SendAsync(message).AsTask());

        session.Executed.Envelopes().Where(x => x.Message is SplitMessage5137).Select(x => x.Destination)
            .ShouldBe([oldQueue, queueOf<FirstSplitHandler5137>(), queueOf<SecondSplitHandler5137>()],
                ignoreOrder: true);

        SplitHandlers5137.Received.ShouldBe(
            [(typeof(FirstSplitHandler5137), message.Id), (typeof(SecondSplitHandler5137), message.Id)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task configuring_the_message_types_local_queue_does_not_handle_a_message_twice()
    {
        using var host = await hostWith(opts => opts.LocalQueueFor<SplitMessage5137>().Sequential(),
            typeof(FirstSplitHandler5137), typeof(SecondSplitHandler5137));

        SplitHandlers5137.Received.Clear();
        var message = new SplitMessage5137(Guid.NewGuid());

        await host.SendMessageAndWaitAsync(message);

        host.GetRuntime().RoutingFor(typeof(SplitMessage5137)).Routes
            .OfType<Wolverine.Runtime.Routing.MessageRoute>().Select(x => x.Uri)
            .ShouldBe([queueOf<FirstSplitHandler5137>(), queueOf<SecondSplitHandler5137>()], ignoreOrder: true);

        SplitHandlers5137.Received.ShouldBe(
            [(typeof(FirstSplitHandler5137), message.Id), (typeof(SecondSplitHandler5137), message.Id)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task a_message_from_outside_the_process_is_relayed_to_every_local_handler_type()
    {
        using var host = await hostWith(opts => opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("arrival-5137"),
            typeof(FirstSplitHandler5137), typeof(SecondSplitHandler5137));

        var handler = host.GetRuntime().Handlers.HandlerFor(typeof(SplitMessage5137), endpointNamed(host, "arrival-5137"))
            .ShouldBeOfType<FanoutMessageHandler<SplitMessage5137>>();

        handler.LocalQueueUris.ShouldBe([queueOf<FirstSplitHandler5137>(), queueOf<SecondSplitHandler5137>()],
            ignoreOrder: true);
    }

    [Fact]
    public async Task relaying_a_message_from_outside_does_not_give_the_message_type_a_single_handler()
    {
        using var host = await hostWith(opts => opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("arrival-5137"),
            typeof(FirstSplitHandler5137), typeof(SecondSplitHandler5137));

        var handlers = host.GetRuntime().Handlers;
        handlers.HandlerFor(typeof(SplitMessage5137), endpointNamed(host, "arrival-5137"))
            .ShouldBeOfType<FanoutMessageHandler<SplitMessage5137>>();

        // The relay is for that endpoint only. It used to be cached as the handler of the message type, and then
        // HandlerFor(Type) answered with it or threw depending on whether a message had arrived from outside yet
        Should.Throw<NoHandlerForEndpointException>(() => handlers.HandlerFor(typeof(SplitMessage5137)));
    }

    [Fact]
    public async Task a_handler_type_next_to_one_sticky_to_another_transport_still_gets_the_message()
    {
        using var host = await hostWith(opts =>
            {
                opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("tcp-sticky-5137")
                    .AddStickyHandler(typeof(TcpStickyMixedHandler5137));
                opts.ListenAtPort(PortFinder.GetAvailablePort()).Named("arrival-5137");
            },
            typeof(PlainMixedHandler5137), typeof(TcpStickyMixedHandler5137));

        var handlers = host.GetRuntime().Handlers;

        // The sticky handler runs at its own endpoint
        var sticky = handlers.HandlerFor(typeof(MixedMessage5137), endpointNamed(host, "tcp-sticky-5137"))
            .ShouldBeAssignableTo<MessageHandler>()!;
        MessageHandlerGroup.HandlerTypeOf(sticky.Chain!).ShouldBe(typeof(TcpStickyMixedHandler5137));

        // Anywhere else the message goes to the handler type that is not sticky. This threw before, because not
        // every chain of the message type was on a local queue
        handlers.HandlerFor(typeof(MixedMessage5137), endpointNamed(host, "arrival-5137"))
            .ShouldBeOfType<FanoutMessageHandler<MixedMessage5137>>()
            .LocalQueueUris.ShouldBe([queueOf<PlainMixedHandler5137>()]);
    }

    [Fact]
    public async Task a_message_whose_handlers_are_all_sticky_elsewhere_names_where_they_run()
    {
        using var host = await hostWith(_ => { }, typeof(FirstStickyElsewhere5137), typeof(SecondStickyElsewhere5137));

        var handlers = host.GetRuntime().Handlers;
        var ex = Should.Throw<NoHandlerForEndpointException>(() =>
            handlers.HandlerFor(typeof(StickyElsewhereMessage5137), new LocalQueue("somewhere-else-5137")));

        ex.Uri.ShouldBe(new Uri("local://somewhere-else-5137"));
        ex.Message.ShouldContain($"{typeof(FirstStickyElsewhere5137).FullNameInCode()} (local://sticky-one-5137/)");
        ex.Message.ShouldContain($"{typeof(SecondStickyElsewhere5137).FullNameInCode()} (local://sticky-two-5137/)");
    }
}

public record CollidingMessage5137;

public record SplitMessage5137(Guid Id);

public record MixedMessage5137;

public record StickyElsewhereMessage5137;

public static class SplitHandlers5137
{
    public static readonly List<(Type, Guid)> Received = [];
}

// Hidden from conventional discovery so they cannot leak into every other host in this assembly
[WolverineIgnore]
[StickyHandler("same-queue-5137")]
public class FirstStickyToSameQueue5137
{
    public void Handle(CollidingMessage5137 message)
    {
    }
}

[WolverineIgnore]
[StickyHandler("same-queue-5137")]
public class SecondStickyToSameQueue5137
{
    public void Handle(CollidingMessage5137 message)
    {
    }
}

[WolverineIgnore]
public class FirstSplitHandler5137
{
    public void Handle(SplitMessage5137 message)
    {
        lock (SplitHandlers5137.Received) SplitHandlers5137.Received.Add((GetType(), message.Id));
    }
}

[WolverineIgnore]
public class SecondSplitHandler5137
{
    public void Handle(SplitMessage5137 message)
    {
        lock (SplitHandlers5137.Received) SplitHandlers5137.Received.Add((GetType(), message.Id));
    }
}

[WolverineIgnore]
public class PlainMixedHandler5137
{
    public void Handle(MixedMessage5137 message)
    {
    }
}

[WolverineIgnore]
public class TcpStickyMixedHandler5137
{
    public void Handle(MixedMessage5137 message)
    {
    }
}

[WolverineIgnore]
[StickyHandler("sticky-one-5137")]
public class FirstStickyElsewhere5137
{
    public void Handle(StickyElsewhereMessage5137 message)
    {
    }
}

[WolverineIgnore]
[StickyHandler("sticky-two-5137")]
public class SecondStickyElsewhere5137
{
    public void Handle(StickyElsewhereMessage5137 message)
    {
    }
}
