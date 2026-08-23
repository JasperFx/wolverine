using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel3988;

// GH-4000: the derived slice attached to the *route*, not only to the assembled model. An RPC forwards
// its request to the bus, so the slice a GrpcRpcDescriptor carries is the forwarded message's slice with
// the RPC stamped on as its trigger — the same slice, derived the same way, that
// ServiceCapabilities.EventModel carries for it.
public class event_model_per_rpc_4000 : IAsyncLifetime
{
    private IHost _host = null!;

    private static GrpcEndpointDescriptor rpc<TRequest>(string method) => new(
        "Orders", method, typeof(TRequest), null, typeof(PlaceOrderHandler),
        GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary);

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "event-model-4000";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PlaceOrderHandler));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private WolverineOptions theOptions => _host.Services.GetRequiredService<WolverineOptions>();

    [Fact]
    public void an_rpc_forwarding_a_handled_message_carries_that_messages_whole_slice()
    {
        var slice = WolverineEventModelSource.ForGrpcEndpoint(rpc<PlaceOrder>("Place"), theOptions);

        slice.Name.ShouldBe(nameof(PlaceOrder));
        slice.CommandType!.Name.ShouldBe(nameof(PlaceOrder));
        // the handler roles are the point: an RPC descriptor reader sees what the message does, not
        // merely that something was forwarded
        slice.HandlerType!.Name.ShouldBe(nameof(PlaceOrderHandler));
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlacedNotification) });

        slice.TriggerKind.ShouldBe(TriggerKind.Grpc);
        slice.TriggerOrigin!.GrpcService.ShouldBe("Orders");
        slice.TriggerOrigin.GrpcMethod.ShouldBe("Place");
        slice.TriggerOrigin.Label.ShouldBe("Orders/Place");
    }

    [Fact]
    public void an_rpc_whose_message_nothing_here_handles_is_trigger_only()
    {
        var slice = WolverineEventModelSource.ForGrpcEndpoint(rpc<CancelOrder>("Cancel"), theOptions);

        slice.Name.ShouldBe(nameof(CancelOrder));
        slice.CommandType!.Name.ShouldBe(nameof(CancelOrder));
        slice.HandlerType.ShouldBeNull();
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.Grpc);
        slice.TriggerLabel.ShouldBe("Orders/Cancel");
    }

    [Fact]
    public void without_options_there_is_still_a_trigger_only_slice()
    {
        // the export path can describe a host whose handler graph is not available; the RPC still renders
        var slice = WolverineEventModelSource.ForGrpcEndpoint(rpc<PlaceOrder>("Place"), null);

        slice.Name.ShouldBe(nameof(PlaceOrder));
        slice.HandlerType.ShouldBeNull();
        slice.TriggerKind.ShouldBe(TriggerKind.Grpc);
    }

    [Fact]
    public void the_per_rpc_slice_is_the_slice_the_assembled_model_carries()
    {
        var endpoint = rpc<PlaceOrder>("Place");
        var perRpc = WolverineEventModelSource.ForGrpcEndpoint(endpoint, theOptions);

        var assembled = WolverineEventModelSource.Describe(theOptions, new StubGrpcManifest(endpoint))
            .Slices.Single(x => x.Name == nameof(PlaceOrder));

        perRpc.Name.ShouldBe(assembled.Name);
        perRpc.CommandType!.FullName.ShouldBe(assembled.CommandType!.FullName);
        perRpc.HandlerType!.FullName.ShouldBe(assembled.HandlerType!.FullName);
        perRpc.Pattern.ShouldBe(assembled.Pattern);
        perRpc.TriggerKind.ShouldBe(assembled.TriggerKind);
        perRpc.TriggerOrigin!.Label.ShouldBe(assembled.TriggerOrigin!.Label);
        perRpc.EmittedEvents.Select(x => x.FullName).ShouldBe(assembled.EmittedEvents.Select(x => x.FullName));
        perRpc.PublishedMessages.Select(x => x.FullName).ShouldBe(assembled.PublishedMessages.Select(x => x.FullName));
        perRpc.AggregateTypes.Select(x => x.FullName).ShouldBe(assembled.AggregateTypes.Select(x => x.FullName));
        perRpc.ReadModelTypes.Select(x => x.FullName).ShouldBe(assembled.ReadModelTypes.Select(x => x.FullName));
    }

    private sealed class StubGrpcManifest(params GrpcEndpointDescriptor[] endpoints) : IGrpcEndpointManifest
    {
        public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; } = endpoints;
    }
}
