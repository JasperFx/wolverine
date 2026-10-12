using JasperFx.Core.Reflection;
using JasperFx.Events.EventModeling;
using Wolverine.Configuration;
using Wolverine.Configuration.EventModeling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel3988;

// jasperfx#1007. A board says WHO triggers a command -- a person, TriggerKind.Human -- and the code says
// HOW it is received: a message handler, an HTTP route, a gRPC call. Those answer different questions, so
// merging them is not a disagreement. The merged slice keeps the how on TriggerKind and the who on
// TriggeredBy. And every slice names the code the user wrote -- their handler class and method -- never
// the MessageHandler / HttpHandler subclass Wolverine generates around it.
public class event_model_human_trigger_and_handler_method_1007
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    private static EventModelDescriptor board(string sliceName)
    {
        var builder = new EventModelBuilder();
        builder.Slice(sliceName).TriggeredBy("Place an order", TriggerKind.Human);
        return builder.Build("app").WithProvenance(EventModelProvenance.Declared);
    }

    private static EventModelSliceDescriptor merge(EventModelDescriptor declared, EventModelSliceDescriptor derived)
        => EventModelDescriptor.Merge("app", new[]
        {
            declared,
            new EventModelDescriptor("app", new[] { derived }).WithProvenance(EventModelProvenance.Derived)
        }).Slices.Single();

    [Fact]
    public void a_message_handler_slice_names_the_users_handler_class_and_method()
    {
        var chain = chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!));
        var slice = EventModelRoles.ForHandlerChain(chain);

        slice.HandlerType!.FullName.ShouldBe(typeof(PlaceOrderHandler).FullName);
        slice.HandlerMethod.ShouldBe(nameof(PlaceOrderHandler.Handle));

        // never the generated MessageHandler subclass, whose name is the chain's TypeName
        slice.HandlerType.Name.ShouldNotBe(chain.TypeName);
        typeof(PlaceOrderHandler).CanBeCastTo<MessageHandler>().ShouldBeFalse();
    }

    [Fact]
    public void a_grpc_rpc_forwarding_a_handled_message_keeps_the_handlers_method()
    {
        var place = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!)));
        var manifest = new StubGrpcManifest(new GrpcEndpointDescriptor("Orders", "Place", typeof(PlaceOrder), null,
            typeof(PlaceOrderGrpcService), GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));

        var slice = WolverineEventModelSource.ApplyGrpcTriggers(new EventModelDescriptor("app", new[] { place }), manifest)
            .Slices.Single();

        // the RPC is the trigger; the code that handles the command is still the handler
        slice.TriggerKind.ShouldBe(TriggerKind.Grpc);
        slice.HandlerType!.Name.ShouldBe(nameof(PlaceOrderHandler));
        slice.HandlerMethod.ShouldBe(nameof(PlaceOrderHandler.Handle));
    }

    [Fact]
    public void a_trigger_only_grpc_slice_names_the_users_service_type_and_rpc_method()
    {
        var manifest = new StubGrpcManifest(new GrpcEndpointDescriptor("Orders", "Cancel", typeof(CancelOrder), null,
            typeof(PlaceOrderGrpcService), GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));

        var slice = WolverineEventModelSource.ApplyGrpcTriggers(new EventModelDescriptor("app", Array.Empty<EventModelSliceDescriptor>()), manifest)
            .Slices.Single();

        slice.HandlerType!.FullName.ShouldBe(typeof(PlaceOrderGrpcService).FullName);
        slice.HandlerMethod.ShouldBe("Cancel");
    }

    [Fact]
    public void a_declared_human_trigger_and_a_message_handler_merge_without_a_disagreement()
    {
        var derived = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!)));

        var slice = merge(board(nameof(PlaceOrder)), derived);

        slice.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
        slice.Hotspots.ShouldNotContain(x => x.Role == EventModelRole.TriggerKind);

        // how: the code; who: the board
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        slice.TriggeredBy.ShouldBe(TriggerKind.Human);
        slice.IsTriggeredByHuman().ShouldBeTrue();

        // and the declaration's label is not lost
        slice.TriggerLabel.ShouldBe("Place an order");
        slice.HandlerType!.Name.ShouldBe(nameof(PlaceOrderHandler));
        slice.HandlerMethod.ShouldBe(nameof(PlaceOrderHandler.Handle));
    }

    [Fact]
    public void a_declared_human_trigger_and_a_grpc_rpc_merge_without_a_disagreement()
    {
        var place = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!)));
        var manifest = new StubGrpcManifest(new GrpcEndpointDescriptor("Orders", "Place", typeof(PlaceOrder), null,
            typeof(PlaceOrderGrpcService), GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));
        var derived = WolverineEventModelSource.ApplyGrpcTriggers(new EventModelDescriptor("app", new[] { place }), manifest)
            .Slices.Single();

        var slice = merge(board(nameof(PlaceOrder)), derived);

        slice.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
        slice.TriggerKind.ShouldBe(TriggerKind.Grpc);
        slice.TriggeredBy.ShouldBe(TriggerKind.Human);
    }

    [Fact]
    public void a_declared_human_trigger_still_disagrees_with_the_job_scheduler()
    {
        // a timeout is not something a person does, so this one is a real finding
        var derived = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!))) with
        {
            TriggerKind = TriggerKind.JobScheduler
        };

        var slice = merge(board(nameof(PlaceOrder)), derived);

        slice.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.SourceDisagreement && x.Role == EventModelRole.TriggerKind);
        slice.TriggeredBy.ShouldBeNull();
    }

    [Fact]
    public void describe_event_model_says_who_and_how_and_names_the_handler_method()
    {
        var derived = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!)));
        var merged = EventModelDescriptor.Merge("app", new[]
        {
            board(nameof(PlaceOrder)),
            new EventModelDescriptor("app", new[] { derived }).WithProvenance(EventModelProvenance.Derived)
        });

        var text = EventModelReport.Plain(merged).ReplaceLineEndings("\n");

        text.ShouldContain("<- Human via MessageHandler \"Place an order\"");
        text.ShouldContain($"handled by: {nameof(PlaceOrderHandler)}.{nameof(PlaceOrderHandler.Handle)}");
    }

    private sealed class StubGrpcManifest(params GrpcEndpointDescriptor[] endpoints) : IGrpcEndpointManifest
    {
        public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; } = endpoints;
    }
}

/// <summary>Stands in for a user's code-first gRPC service contract.</summary>
public interface PlaceOrderGrpcService;
