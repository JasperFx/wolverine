using Marten;
using Marten.Exceptions;
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

    [Fact]
    public async Task starting_the_same_process_twice_throws_and_first_start_wins()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 100m));

        // MartenOps.StartStream forbids duplicate stream creation. On a second start with the same
        // id, Marten throws ExistingStreamIdCollisionException and Wolverine propagates it through
        // InvokeMessageAndWaitAsync. No swallowing, no silent drop.
        var thrown = await Should.ThrowAsync<ExistingStreamIdCollisionException>(async () =>
        {
            await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(id, Guid.NewGuid(), 200m));
        });

        thrown.Id.ShouldBe(id);
        thrown.AggregateType.ShouldBe(typeof(OrderFulfillmentState));

        // The first start's data must still be intact; the second start's transaction rolled back.
        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(1);
        var started = events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        started.TotalAmount.ShouldBe(100m);
    }
}
