using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance.EventModel3988;

// GH-3988: every chain derives its own Event Modeling roles — command, handler, aggregates, emitted
// events, read models, published messages, trigger kind, slice pattern — and writes them as the
// JasperFx EventModelSliceDescriptor. Nothing here is declared; it is all read off the chain. These
// tests need no store: the roles are read from the handler signature (and from the chain's tags when
// the aggregate workflow has run), so a chain whose code has not been generated yet reports the same.
public class event_model_roles_on_chains_3988
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void a_plain_handler_with_a_cascading_message_is_a_command_slice_publishing_a_message()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!)));

        slice.Name.ShouldBe(nameof(PlaceOrder));
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        slice.CommandType!.Name.ShouldBe(nameof(PlaceOrder));
        slice.HandlerType!.Name.ShouldBe(nameof(PlaceOrderHandler));
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlacedNotification) });
        slice.EmittedEvents.ShouldBeEmpty();
        slice.AggregateTypes.ShouldBeEmpty();
    }

    [Fact]
    public void write_model_parameter_makes_the_returns_emitted_events_on_that_aggregate()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<ShipOrderHandler>(x => ShipOrderHandler.Handle(null!, null!)));

        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(OrderShipped) });
        slice.PublishedMessages.ShouldBeEmpty();
        slice.Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void decider_function_infers_the_aggregate_from_the_signature_and_reads_every_return_as_an_event()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<ConfirmOrderHandler>(x => ConfirmOrderHandler.Handle(null!, null!)));

        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(OrderConfirmed), nameof(OrderNotified) });
        slice.PublishedMessages.ShouldBeEmpty();
    }

    [Fact]
    public void read_model_and_entity_parameters_are_read_models_and_the_dto_is_a_published_message()
    {
        var readModel = EventModelRoles.ForHandlerChain(chainFor<GetOrderHandler>(x => GetOrderHandler.Handle(null!, null!)));
        readModel.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        readModel.AggregateTypes.ShouldBeEmpty();
        readModel.EmittedEvents.ShouldBeEmpty();
        readModel.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderSummary) });

        var entity = EventModelRoles.ForHandlerChain(chainFor<GetCustomerHandler>(x => GetCustomerHandler.Handle(null!, null!)));
        entity.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Customer) });
    }

    [Fact]
    public void dcb_model_parameter_is_a_boundary_aggregate()
    {
        var chain = chainFor<ChangeCourseCapacityHandler>(x => ChangeCourseCapacityHandler.Handle(null!, null!));
        var slice = EventModelRoles.ForHandlerChain(chain);

        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(CourseState) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(CourseCapacityChanged) });

        var aggregate = EventModelRoles.AggregatesFor(chain).ShouldHaveSingleItem();
        aggregate.Type.Name.ShouldBe(nameof(CourseState));
        aggregate.Kind.ShouldBe(AggregateKind.BoundaryModel);
    }

    [Fact]
    public void storage_action_returns_are_read_models_the_slice_produces()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<RegisterCustomerHandler>(x => RegisterCustomerHandler.Handle(null!)));

        slice.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Customer) });
        slice.PublishedMessages.ShouldBeEmpty();
        slice.EmittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void a_timeout_message_is_triggered_by_the_job_scheduler()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<ShipmentTimeoutHandler>(x => ShipmentTimeoutHandler.Handle(null!)));
        slice.TriggerKind.ShouldBe(TriggerKind.JobScheduler);
    }

    [Fact]
    public void a_saga_returned_from_its_own_start_is_not_a_published_message()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<OrderSaga>(x => OrderSaga.Start(null!)));
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlacedNotification) });
    }

    [Fact]
    public void the_aggregate_element_carries_the_events_the_type_applies()
    {
        var chain = chainFor<ShipOrderHandler>(x => ShipOrderHandler.Handle(null!, null!));
        var aggregate = EventModelRoles.AggregatesFor(chain).ShouldHaveSingleItem();

        aggregate.Type.Name.ShouldBe(nameof(Order));
        aggregate.Kind.ShouldBe(AggregateKind.WriteAggregate);
        aggregate.AppliedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlaced), nameof(OrderShipped), nameof(OrderConfirmed) });
    }

    [Fact]
    public void a_slice_whose_command_is_an_event_another_slice_emits_is_an_automation()
    {
        var ship = EventModelRoles.ForHandlerChain(chainFor<ShipOrderHandler>(x => ShipOrderHandler.Handle(null!, null!)));
        var reaction = EventModelRoles.ForHandlerChain(chainFor<OrderShippedHandler>(x => OrderShippedHandler.Handle(null!)));
        reaction.Pattern.ShouldBe(SlicePattern.Command); // on its own it looks like a command slice

        var model = WolverineEventModelSource.FinishModel(new EventModelDescriptor("app", new[] { ship, reaction }));

        model.Slices.Single(x => x.Name == nameof(OrderShipped)).Pattern.ShouldBe(SlicePattern.Automation);
        model.Slices.Single(x => x.Name == nameof(ShipOrder)).Pattern.ShouldBe(SlicePattern.Command);
    }

    [Fact]
    public void a_grpc_rpc_that_forwards_a_message_is_that_slices_trigger()
    {
        var place = EventModelRoles.ForHandlerChain(chainFor<PlaceOrderHandler>(x => PlaceOrderHandler.Handle(null!)));
        var model = new EventModelDescriptor("app", new[] { place });

        var manifest = new StubGrpcManifest(
            new GrpcEndpointDescriptor("Orders", "Place", typeof(PlaceOrder), null, typeof(PlaceOrderHandler), GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary),
            new GrpcEndpointDescriptor("Orders", "Cancel", typeof(CancelOrder), null, typeof(PlaceOrderHandler), GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));

        var applied = WolverineEventModelSource.ApplyGrpcTriggers(model, manifest);

        var placeSlice = applied.Slices.Single(x => x.Name == nameof(PlaceOrder));
        placeSlice.TriggerKind.ShouldBe(TriggerKind.Grpc);
        placeSlice.TriggerOrigin!.GrpcService.ShouldBe("Orders");
        placeSlice.TriggerOrigin.GrpcMethod.ShouldBe("Place");
        placeSlice.HandlerType!.Name.ShouldBe(nameof(PlaceOrderHandler)); // the handler roles survive

        // No handler chain for CancelOrder in this process: a trigger-only slice so the RPC still renders
        var cancel = applied.Slices.Single(x => x.Name == nameof(CancelOrder));
        cancel.TriggerKind.ShouldBe(TriggerKind.Grpc);
        cancel.CommandType!.Name.ShouldBe(nameof(CancelOrder));
        cancel.HandlerType.ShouldBeNull();
    }

    private sealed class StubGrpcManifest(params GrpcEndpointDescriptor[] endpoints) : IGrpcEndpointManifest
    {
        public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; } = endpoints;
    }
}

// GH-3988 / GH-3990: the roles reach the outside through the registered IEventModelDefinitionSource,
// the ServiceCapabilities snapshot, and the event-model export — all from a booted host, none of
// which needs a source generator.
public class event_model_sources_and_capabilities_3988 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Registered BEFORE UseWolverine() on purpose: the derived source must still win on
                // merge, so the overlay may only fill gaps (the trigger label) — never a derived role
                services.AddEventModel("Overlay", model =>
                {
                    model.InDomain("Sales");
                    model.Slice(nameof(PlaceOrder)).TriggeredBy("UI: Place order");
                });
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "event-model-3988";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PlaceOrderHandler))
                    .IncludeType(typeof(ShipmentTimeoutHandler));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void the_wolverine_source_is_registered_first()
    {
        var sources = _host.Services.GetServices<IEventModelDefinitionSource>().ToArray();
        sources.First().ShouldBeOfType<WolverineEventModelSource>();
        sources.Length.ShouldBe(2);
    }

    [Fact]
    public async Task discovery_assembles_the_derived_roles_with_the_overlay_only_filling_gaps()
    {
        var model = await WolverineEventModelExport.AssembleAsync(_host.Services, token: TestContext.Current.CancellationToken);

        model.Name.ShouldBe("event-model-3988");
        var slice = model.Slices.Single(x => x.Name == nameof(PlaceOrder));

        // derived
        slice.CommandType!.Name.ShouldBe(nameof(PlaceOrder));
        slice.HandlerType!.Name.ShouldBe(nameof(PlaceOrderHandler));
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlacedNotification) });
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);

        // overlay
        slice.TriggerLabel.ShouldBe("UI: Place order");
        slice.Domain.ShouldBe("Sales");

        model.Slices.Single(x => x.Name == nameof(ShipmentTimeout)).TriggerKind.ShouldBe(TriggerKind.JobScheduler);
    }

    [Fact]
    public async Task the_capabilities_snapshot_carries_the_model_and_the_per_handler_slice()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);

        capabilities.EventModel.ShouldNotBeNull();
        capabilities.EventModel.Name.ShouldBe("event-model-3988");
        capabilities.EventModel.Slices.Select(x => x.Name).ShouldContain(nameof(PlaceOrder));

        var handler = capabilities.Messages.Single(x => x.Type.Name == nameof(PlaceOrder)).Handlers.Single();
        handler.EventModel.ShouldNotBeNull();
        handler.EventModel.CommandType!.Name.ShouldBe(nameof(PlaceOrder));
        handler.EventModel.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlacedNotification) });
    }

    [Fact]
    public async Task the_export_round_trips_through_the_wire_descriptor()
    {
        var model = await WolverineEventModelExport.AssembleAsync(_host.Services, token: TestContext.Current.CancellationToken);

        var json = WolverineEventModelExport.ToJson(model);

        // the wire shape CritterWatch serialises: camelCase, enums as strings, rendering contract present
        json.ShouldContain("\"triggerKind\": \"MessageHandler\"");
        json.ShouldContain("\"pattern\": \"Command\"");
        json.ShouldContain("\"elements\"");
        json.ShouldContain("\"edges\"");

        var back = WolverineEventModelExport.FromJson(json)!;
        back.Name.ShouldBe(model.Name);
        back.Slices.Select(x => x.Name).ShouldBe(model.Slices.Select(x => x.Name));
        var slice = back.Slices.Single(x => x.Name == nameof(PlaceOrder));
        slice.CommandType!.FullName.ShouldBe(typeof(PlaceOrder).FullName);
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(OrderPlacedNotification) });
        slice.TriggerLabel.ShouldBe("UI: Place order");
        slice.Elements.Count.ShouldBe(model.Slices.Single(x => x.Name == nameof(PlaceOrder)).Elements.Count);
    }
}

#region sample types for GH-3988

public record PlaceOrder(string OrderId);
public record CancelOrder(string OrderId);
public record OrderPlacedNotification(string OrderId);

public class PlaceOrderHandler
{
    public static OrderPlacedNotification Handle(PlaceOrder command) => new(command.OrderId);
}

public record ShipOrder(string OrderId);
public record ConfirmOrder(string OrderId);
public record GetOrder(string OrderId);
public record OrderPlaced;
public record OrderShipped;
public record OrderConfirmed;
public record OrderNotified;
public record OrderSummary(string OrderId);

public class Order
{
    public string Id { get; set; } = null!;
    public static Order Create(OrderPlaced placed) => new();
    public void Apply(OrderShipped shipped) { }
    public void Apply(OrderConfirmed confirmed) { }
}

public class ShipOrderHandler
{
    public static OrderShipped Handle(ShipOrder command, [WriteModel] Order order) => new();
}

public class ConfirmOrderHandler
{
    [DeciderFunction]
    public static (OrderConfirmed, OrderNotified) Handle(ConfirmOrder command, Order order) => (new(), new());
}

public class GetOrderHandler
{
    public static OrderSummary Handle(GetOrder query, [ReadModel] Order order) => new(order.Id);
}

public class OrderShippedHandler
{
    public static void Handle(OrderShipped shipped) { }
}

public record GetCustomer(string CustomerId);
public record RegisterCustomer(string CustomerId);
public class Customer { public string Id { get; set; } = null!; }

public class GetCustomerHandler
{
    public static OrderSummary Handle(GetCustomer query, [Entity] Customer customer) => new(customer.Id);
}

public class RegisterCustomerHandler
{
    public static IStorageAction<Customer> Handle(RegisterCustomer command) => Storage.Insert(new Customer { Id = command.CustomerId });
}

public record ChangeCourseCapacity(string CourseId, int Capacity);
public record CourseCapacityChanged(int Capacity);
public class CourseState { public int Capacity { get; set; } }

public class ChangeCourseCapacityHandler
{
    public static CourseCapacityChanged Handle(ChangeCourseCapacity command, [DcbModel] CourseState state) => new(command.Capacity);
}

public record ShipmentTimeout(string OrderId) : TimeoutMessage(TimeSpan.FromMinutes(5));

public class ShipmentTimeoutHandler
{
    public static void Handle(ShipmentTimeout timeout) { }
}

public class OrderSaga : Saga
{
    public string Id { get; set; } = null!;

    public static (OrderSaga, OrderPlacedNotification) Start(PlaceOrder command) => (new OrderSaga { Id = command.OrderId }, new(command.OrderId));

    public void Handle(OrderShipped shipped) => MarkCompleted();
}

#endregion
