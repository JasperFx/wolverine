using GreeterCodeFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Messages;
using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Xunit;
using ProtoGreeterHandler = GreeterProtoFirstGrpc.Server.GreeterHandler;

namespace Wolverine.Grpc.Tests;

// GH-4000: the Event Model slice reaches CritterWatch *next to the RPC that starts it*, not only through
// the assembled ServiceCapabilities.EventModel. Because the generated wrapper forwards the request to the
// bus, the slice on a GrpcRpcDescriptor is the forwarded message's whole slice — handler, aggregates,
// events, cascaded messages — with the RPC stamped on as the trigger.
[Collection(GrpcSerialTestsCollection.Name)]
public class grpc_event_model_slice_4000 : IClassFixture<GrpcCapabilitiesFixture>
{
    private readonly GrpcCapabilitiesFixture _fixture;

    public grpc_event_model_slice_4000(GrpcCapabilitiesFixture fixture)
    {
        _fixture = fixture;
    }

    private GrpcRpcDescriptor source(GrpcServiceDiscoveryMode mode, string method)
        => _fixture.SourceEndpoints.Single(e => e.Mode == mode && e.MethodName == method);

    [Fact]
    public void every_rpc_carries_a_slice_triggered_by_itself()
    {
        _fixture.SourceEndpoints.ShouldNotBeEmpty();

        foreach (var endpoint in _fixture.SourceEndpoints)
        {
            var slice = endpoint.EventModel.ShouldNotBeNull();

            slice.TriggerKind.ShouldBe(TriggerKind.Grpc);
            slice.TriggerOrigin.ShouldNotBeNull();
            slice.TriggerOrigin.GrpcService.ShouldBe(endpoint.ServiceName);
            slice.TriggerOrigin.GrpcMethod.ShouldBe(endpoint.MethodName);
            slice.CommandType!.FullName.ShouldBe(endpoint.RequestType!.FullName);
        }
    }

    [Fact]
    public void the_capabilities_snapshot_carries_the_slice_on_every_rpc()
    {
        // the payload a monitoring console actually reads
        _fixture.Capabilities.GrpcEndpoints.ShouldNotBeEmpty();
        _fixture.Capabilities.GrpcEndpoints.ShouldAllBe(e =>
            e.EventModel != null && e.EventModel.TriggerKind == TriggerKind.Grpc);

        var sayHello = _fixture.Capabilities.GrpcEndpoints
            .Single(e => e.ServiceName == "Greeter" && e.MethodName == "SayHello");
        sayHello.EventModel!.HandlerType!.FullName.ShouldBe(typeof(ProtoGreeterHandler).FullName);
    }

    [Fact]
    public void the_proto_first_unary_slice_carries_the_forwarded_messages_handler_roles()
    {
        var slice = source(GrpcServiceDiscoveryMode.ProtoFirst, "SayHello").EventModel.ShouldNotBeNull();

        slice.Name.ShouldBe(nameof(HelloRequest));
        slice.CommandType!.FullName.ShouldBe(typeof(HelloRequest).FullName);
        slice.HandlerType!.FullName.ShouldBe(typeof(ProtoGreeterHandler).FullName);
        slice.TriggerOrigin!.Label.ShouldBe("Greeter/SayHello");
    }

    // This host discovers the code-first *contract* assembly but not its handlers, which is exactly the
    // case an RPC forwarding a message nothing in the process handles has to cover: there is still a
    // boundary to render, so the slice is trigger-only rather than absent.
    [Fact]
    public void an_rpc_whose_message_nothing_here_handles_is_trigger_only()
    {
        var slice = source(GrpcServiceDiscoveryMode.CodeFirst, "Greet").EventModel.ShouldNotBeNull();

        slice.Name.ShouldBe(nameof(GreetRequest));
        slice.CommandType!.FullName.ShouldBe(typeof(GreetRequest).FullName);
        slice.HandlerType.ShouldBeNull();
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerOrigin!.Label.ShouldBe("GreeterCodeFirstService/Greet");
    }

    // The acceptance criterion of GH-4000: the slice on the route and the slice in the assembled model are
    // the same slice, because they are derived once by the same code.
    [Fact]
    public void each_rpc_slice_is_the_slice_the_assembled_model_carries_for_it()
    {
        var model = _fixture.Capabilities.EventModel.ShouldNotBeNull();

        foreach (var endpoint in _fixture.SourceEndpoints)
        {
            var perRpc = endpoint.EventModel.ShouldNotBeNull();
            var assembled = model.Slices
                .FirstOrDefault(x => x.CommandType?.FullName == endpoint.RequestType!.FullName);

            if (assembled is null)
            {
                // The assembled model merges slices by NAME, so two unrelated messages whose simple names
                // collide -- the proto-first and code-first StreamGreetingsRequest here -- fold into one
                // slice and the loser has no slice of its own left to compare against. Nothing folds per
                // route, which is one more reason the route-attached copy is worth carrying.
                model.Slices.ShouldContain(x => x.Name == endpoint.RequestType!.Name);
                continue;
            }

            assembled.Name.ShouldBe(perRpc.Name);
            assembled.HandlerType?.FullName.ShouldBe(perRpc.HandlerType?.FullName);
            assembled.Pattern.ShouldBe(perRpc.Pattern);
            assembled.TriggerKind.ShouldBe(perRpc.TriggerKind);
            assembled.EmittedEvents.Select(x => x.FullName)
                .ShouldBe(perRpc.EmittedEvents.Select(x => x.FullName));
            assembled.PublishedMessages.Select(x => x.FullName)
                .ShouldBe(perRpc.PublishedMessages.Select(x => x.FullName));
            assembled.AggregateTypes.Select(x => x.FullName)
                .ShouldBe(perRpc.AggregateTypes.Select(x => x.FullName));
            assembled.ReadModelTypes.Select(x => x.FullName)
                .ShouldBe(perRpc.ReadModelTypes.Select(x => x.FullName));
        }
    }

    // ...with one honest exception, and it is the reason attaching the slice per route is worth doing.
    // The model folds every source's view of a message into ONE slice, so when several RPCs forward the
    // same message it can only name one of them as the trigger — here SayHello and the client-streaming
    // CollectGreetings both forward HelloRequest, and the model keeps whichever it stamped first. The
    // copy hanging off a route names *that* route, so an operator reading the RPC sees its own trigger.
    [Fact]
    public void a_message_forwarded_by_more_than_one_rpc_keeps_its_own_trigger_per_route()
    {
        var forwardingHello = _fixture.SourceEndpoints
            .Where(x => x.RequestType!.FullName == typeof(HelloRequest).FullName)
            .ToArray();

        forwardingHello.Length.ShouldBeGreaterThan(1);
        forwardingHello.Select(x => x.EventModel!.TriggerOrigin!.Label)
            .ShouldBe(forwardingHello.Select(x => $"{x.ServiceName}/{x.MethodName}"));

        var assembled = _fixture.Capabilities.EventModel!.Slices
            .First(x => x.CommandType?.FullName == typeof(HelloRequest).FullName);

        // the model's single trigger is one of them — the first in service::method order
        assembled.TriggerOrigin!.Label
            .ShouldBe(forwardingHello.Select(x => $"{x.ServiceName}/{x.MethodName}")
                .OrderBy(x => x, StringComparer.Ordinal).First());
    }
}
