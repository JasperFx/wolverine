using Marten;
using ProcessManagerSample.OrderFulfillment;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace ProcessManagerSample.Tests.OrderFulfillment;

public class when_cancelling_a_fulfillment : IntegrationContext
{
    public when_cancelling_a_fulfillment(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task cancel_mid_process_marks_the_stream_cancelled()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 42m));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 42m));
        await Host.InvokeMessageAndWaitAsync(new CancelOrderFulfillment(id, "Fraud suspected"));

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(3);
        var cancelled = events[2].Data.ShouldBeOfType<OrderFulfillmentCancelled>();
        cancelled.Reason.ShouldBe("Fraud suspected");

        var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
        state.ShouldNotBeNull();
        state.IsCancelled.ShouldBeTrue();
        state.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task integration_events_after_cancellation_are_ignored()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 99m));
        await Host.InvokeMessageAndWaitAsync(new CancelOrderFulfillment(id, "Customer changed mind"));

        // Warehouse did not know about the cancellation in time and still sends a reservation event.
        await Host.InvokeMessageAndWaitAsync(new ItemsReserved(id, Guid.NewGuid()));
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 99m));

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        events[1].Data.ShouldBeOfType<OrderFulfillmentCancelled>();
    }

    [Fact]
    public async Task second_cancel_is_a_no_op()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 5m));
        await Host.InvokeMessageAndWaitAsync(new CancelOrderFulfillment(id, "First reason"));
        await Host.InvokeMessageAndWaitAsync(new CancelOrderFulfillment(id, "Second reason"));

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<OrderFulfillmentCancelled>().Reason.ShouldBe("First reason");
    }
}
