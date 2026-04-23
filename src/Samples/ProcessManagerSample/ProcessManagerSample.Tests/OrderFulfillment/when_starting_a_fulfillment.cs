using Marten;
using ProcessManagerSample.OrderFulfillment;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace ProcessManagerSample.Tests.OrderFulfillment;

public class when_starting_a_fulfillment : IntegrationContext
{
    public when_starting_a_fulfillment(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task creates_the_stream_with_the_started_event()
    {
        var command = new StartOrderFulfillment(
            OrderFulfillmentStateId: Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            TotalAmount: 129.99m);

        // InvokeMessageAndWaitAsync drives the handler through Wolverine, including the
        // Marten transaction. When it returns, the first AppendOne has been committed.
        await Host.InvokeMessageAndWaitAsync(command);

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(command.OrderFulfillmentStateId);

        events.Count.ShouldBe(1);
        var started = events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        started.OrderFulfillmentStateId.ShouldBe(command.OrderFulfillmentStateId);
        started.CustomerId.ShouldBe(command.CustomerId);
        started.TotalAmount.ShouldBe(command.TotalAmount);

        // Inline snapshot must have projected the event into the aggregate document.
        var state = await session.Events.FetchLatest<OrderFulfillmentState>(command.OrderFulfillmentStateId);
        state.ShouldNotBeNull();
        state.Id.ShouldBe(command.OrderFulfillmentStateId);
        state.CustomerId.ShouldBe(command.CustomerId);
        state.TotalAmount.ShouldBe(command.TotalAmount);
        state.IsTerminal.ShouldBeFalse();
    }
}
