using Marten;
using ProcessManagerViaHandlers.OrderFulfillment;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace ProcessManagerViaHandlers.Tests.OrderFulfillment;

public class when_completing_a_fulfillment : IntegrationContext
{
    public when_completing_a_fulfillment(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task happy_path_ends_with_OrderFulfillmentCompleted()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 249.00m));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 249.00m));
        await Host.InvokeMessageAndWaitAsync(new ItemsReserved(id, Guid.NewGuid()));
        await Host.InvokeMessageAndWaitAsync(new ShipmentConfirmed(id, "TRACK-ABC"));

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(5);
        events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        events[1].Data.ShouldBeOfType<PaymentConfirmed>();
        events[2].Data.ShouldBeOfType<ItemsReserved>();
        events[3].Data.ShouldBeOfType<ShipmentConfirmed>();
        events[4].Data.ShouldBeOfType<OrderFulfillmentCompleted>();

        var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();
        state.IsCancelled.ShouldBeFalse();
        state.PaymentConfirmed.ShouldBeTrue();
        state.ItemsReserved.ShouldBeTrue();
        state.ShipmentConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task messages_arriving_out_of_order_still_complete_the_process()
    {
        var id = Guid.NewGuid();

        // Payment is deliberately last here. Any permutation of the three gates should complete.
        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 50m));
        await Host.InvokeMessageAndWaitAsync(new ShipmentConfirmed(id, "TRACK-1"));
        await Host.InvokeMessageAndWaitAsync(new ItemsReserved(id, Guid.NewGuid()));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 50m));

        await using var session = Store.LightweightSession();
        var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task duplicate_integration_event_is_a_no_op()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 75m));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 75m));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 75m));

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        events[1].Data.ShouldBeOfType<PaymentConfirmed>();
    }

    [Fact]
    public async Task integration_events_after_completion_are_ignored()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 10m));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 10m));
        await Host.InvokeMessageAndWaitAsync(new ItemsReserved(id, Guid.NewGuid()));
        await Host.InvokeMessageAndWaitAsync(new ShipmentConfirmed(id, "TRACK-1"));

        // Duplicate ShipmentConfirmed after terminal state. The completion guard must ignore it.
        await Host.InvokeMessageAndWaitAsync(new ShipmentConfirmed(id, "TRACK-2"));

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);
        events.Count.ShouldBe(5);
    }
}
