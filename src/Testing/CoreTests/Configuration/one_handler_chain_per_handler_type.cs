using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Transports.Local;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5136. Wolverine 7 builds one handler chain per (message type, handler type), whatever
/// MultipleHandlerBehavior says. Every host here leaves MultipleHandlerBehavior at its default.
/// </summary>
public class one_handler_chain_per_handler_type
{
    private static async Task<IHost> hostWith(params Type[] handlerTypes)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);
            }).StartAsync();
    }

    private static HandlerChain[] chainsFor<T>(IHost host)
        => host.GetRuntime().Handlers.AllChains().Where(x => x.MessageType == typeof(T)).ToArray();

    [Fact]
    public async Task a_message_with_one_handler_type_keeps_the_message_type_naming_and_queue()
    {
        using var host = await hostWith(typeof(SoloHandler5136));

        var chain = chainsFor<SoloMessage5136>(host).ShouldHaveSingleItem();

        // So that pre-generated code survives the upgrade
        chain.TypeName.ShouldBe(HandlerChain.GeneratedTypeNameFor(typeof(SoloMessage5136), HandlerChain.HandlerSuffix));
        chain.Endpoints.ShouldBeEmpty();

        host.GetRuntime().Handlers.ChainFor<SoloMessage5136>().ShouldBeSameAs(chain);

        host.GetRuntime().RoutingFor(typeof(SoloMessage5136)).Routes.OfType<Wolverine.Runtime.Routing.MessageRoute>().Single().Uri
            .ShouldBe(new Uri("local://" + typeof(SoloMessage5136).ToMessageTypeName().ToLowerInvariant()));
    }

    [Fact]
    public async Task two_handler_types_get_two_chains_on_their_own_queues()
    {
        using var host = await hostWith(typeof(FirstSharedHandler5136), typeof(SecondSharedHandler5136));

        var chains = chainsFor<SharedMessage5136>(host);
        chains.Length.ShouldBe(2);

        foreach (var chain in chains)
        {
            var handlerType = chain.Handlers.ShouldHaveSingleItem().HandlerType;
            chain.Endpoints.ShouldHaveSingleItem().ShouldBeOfType<LocalQueue>()
                .EndpointName.ShouldBe(handlerType.FullNameInCode().ToLowerInvariant());
            chain.TypeName.ShouldBe(HandlerChain.GeneratedTypeNameFor(handlerType, HandlerChain.HandlerSuffix));
        }

        var ex = Should.Throw<AmbiguousHandlerChainException>(() => host.GetRuntime().Handlers.ChainFor<SharedMessage5136>());
        ex.Message.ShouldContain(typeof(FirstSharedHandler5136).FullNameInCode());
        ex.Message.ShouldContain(typeof(SecondSharedHandler5136).FullNameInCode());
    }

    [Fact]
    public async Task two_handler_types_both_handle_a_published_message()
    {
        using var host = await hostWith(typeof(FirstSharedHandler5136), typeof(SecondSharedHandler5136));

        var session = await host.SendMessageAndWaitAsync(new SharedMessage5136());

        session.Executed.Envelopes()
            .Where(x => x.Message is SharedMessage5136)
            .Select(x => x.Destination)
            .ShouldBe([
                new Uri("local://" + typeof(FirstSharedHandler5136).FullNameInCode().ToLowerInvariant()),
                new Uri("local://" + typeof(SecondSharedHandler5136).FullNameInCode().ToLowerInvariant())
            ], ignoreOrder: true);
    }

    [Fact]
    public async Task a_sticky_attribute_applies_to_a_sole_handler()
    {
        using var host = await hostWith(typeof(SoloStickyHandler5136));

        var chain = chainsFor<SoloStickyMessage5136>(host).ShouldHaveSingleItem();
        chain.Endpoints.ShouldHaveSingleItem().EndpointName.ShouldBe("solo-sticky-5136");

        // Still the only chain of its message type, so it keeps the message type's generated name
        chain.TypeName.ShouldBe(HandlerChain.GeneratedTypeNameFor(typeof(SoloStickyMessage5136), HandlerChain.HandlerSuffix));

        host.GetRuntime().RoutingFor(typeof(SoloStickyMessage5136)).Routes.OfType<Wolverine.Runtime.Routing.MessageRoute>().Single().Uri
            .ShouldBe(new Uri("local://solo-sticky-5136"));
    }

    [Fact]
    public async Task two_methods_on_one_handler_class_for_the_same_message_is_a_startup_error()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => hostWith(typeof(TwoMethodsHandler5136)));

        ex.Message.ShouldContain(typeof(TwoMethodsHandler5136).FullNameInCode());
        ex.Message.ShouldContain(typeof(TwoMethodsMessage5136).FullNameInCode());
        ex.Message.ShouldContain("Handle()");
        ex.Message.ShouldContain("Consume()");
    }

    [Fact]
    public async Task a_sagas_methods_for_one_message_stay_one_chain()
    {
        using var host = await hostWith(typeof(OrderSaga5136));

        var chain = chainsFor<StartOrder5136>(host).ShouldHaveSingleItem().ShouldBeOfType<SagaChain>();
        chain.SagaType.ShouldBe(typeof(OrderSaga5136));
    }

    [Fact]
    public async Task a_saga_and_a_plain_handler_are_two_chains_and_both_run()
    {
        using var host = await hostWith(typeof(OrderSaga5136), typeof(OrderAuditHandler5136));

        var chains = chainsFor<StartOrder5136>(host);
        chains.Length.ShouldBe(2);
        chains.OfType<SagaChain>().ShouldHaveSingleItem().SagaType.ShouldBe(typeof(OrderSaga5136));
        chains.Single(x => x is not SagaChain).Handlers.ShouldHaveSingleItem().HandlerType
            .ShouldBe(typeof(OrderAuditHandler5136));

        // Was the bug in the classic combining mode: the plain handler was silently dropped
        OrderAuditHandler5136.Received.Clear();
        var id = Guid.NewGuid();
        var session = await host.SendMessageAndWaitAsync(new StartOrder5136(id));

        session.Executed.MessagesOf<StartOrder5136>().Count().ShouldBe(2);
        OrderAuditHandler5136.Received.ShouldContain(id);
    }

    [Fact]
    public async Task two_saga_types_for_one_message_need_no_opt_in()
    {
        using var host = await hostWith(typeof(FirstSharedWorkSaga), typeof(SecondSharedWorkSaga));

        chainsFor<StartSharedWork>(host).OfType<SagaChain>().Select(x => x.SagaType)
            .ShouldBe([typeof(FirstSharedWorkSaga), typeof(SecondSharedWorkSaga)], ignoreOrder: true);
    }

    [Fact]
    public async Task a_handler_type_with_two_separated_messages_gets_distinct_generated_names()
    {
        using var host = await hostWith(typeof(TwoMessagesHandler5136), typeof(FirstSharedHandler5136),
            typeof(OtherMessageHandler5136));

        var names = host.GetRuntime().Handlers.AllChains()
            .Where(x => x.Handlers.Single().HandlerType == typeof(TwoMessagesHandler5136))
            .Select(x => x.TypeName)
            .ToArray();

        names.Length.ShouldBe(2);
        names.Distinct().Count().ShouldBe(2);
        names.ShouldAllBe(x => x.Contains("TwoMessagesHandler5136"));
    }
}

public record SoloMessage5136;

public record SharedMessage5136;

public record OtherMessage5136;

public record SoloStickyMessage5136;

public record TwoMethodsMessage5136;

public record StartOrder5136(Guid Id);

// Hidden from conventional discovery so they cannot leak into every other host in this assembly
[WolverineIgnore]
public class SoloHandler5136
{
    public void Handle(SoloMessage5136 message)
    {
    }
}

[WolverineIgnore]
public class FirstSharedHandler5136
{
    public void Handle(SharedMessage5136 message)
    {
    }
}

[WolverineIgnore]
public class SecondSharedHandler5136
{
    public void Handle(SharedMessage5136 message)
    {
    }
}

[WolverineIgnore]
public class TwoMessagesHandler5136
{
    public void Handle(SharedMessage5136 message)
    {
    }

    public void Handle(OtherMessage5136 message)
    {
    }
}

[WolverineIgnore]
public class OtherMessageHandler5136
{
    public void Handle(OtherMessage5136 message)
    {
    }
}

[WolverineIgnore]
[StickyHandler("solo-sticky-5136")]
public class SoloStickyHandler5136
{
    public void Handle(SoloStickyMessage5136 message)
    {
    }
}

[WolverineIgnore]
public class TwoMethodsHandler5136
{
    public void Handle(TwoMethodsMessage5136 message)
    {
    }

    public void Consume(TwoMethodsMessage5136 message)
    {
    }
}

[WolverineIgnore]
public class OrderSaga5136 : Saga
{
    public Guid Id { get; set; }

    public static OrderSaga5136 Start(StartOrder5136 message) => new() { Id = message.Id };

    public void Handle(StartOrder5136 message)
    {
    }
}

[WolverineIgnore]
public class OrderAuditHandler5136
{
    public static readonly List<Guid> Received = new();

    public void Handle(StartOrder5136 message)
    {
        lock (Received)
        {
            Received.Add(message.Id);
        }
    }
}
