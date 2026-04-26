using Marten;
using ProcessManagerSample.OrderFulfillment;
using ProcessManagerSample.OrderFulfillment.Handlers;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace ProcessManagerSample.Tests.OrderFulfillment;

public class when_payment_times_out : IntegrationContext
{
    // How long to wait for the scheduler to pick up and dispatch the PaymentTimeout.
    // The scheduler polls on an interval, so the actual fire time is "around" the requested delay
    // plus one poll cycle. Keep the observation window comfortably larger than both combined.
    private static readonly TimeSpan SchedulerObservationWindow = TimeSpan.FromSeconds(10);

    public when_payment_times_out(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void unit_timeout_cancels_when_payment_not_yet_confirmed()
    {
        var state = new OrderFulfillmentState { Id = Guid.NewGuid() };

        var result = PaymentTimeoutHandler.Handle(new PaymentTimeout(state.Id), state);

        result.Count.ShouldBe(1);
        var cancelled = result[0].ShouldBeOfType<OrderFulfillmentCancelled>();
        cancelled.Reason.ShouldBe("Payment timed out");
    }

    [Fact]
    public void unit_timeout_is_no_op_when_payment_already_confirmed()
    {
        var state = new OrderFulfillmentState
        {
            Id = Guid.NewGuid(),
            PaymentConfirmed = true
        };

        var result = PaymentTimeoutHandler.Handle(new PaymentTimeout(state.Id), state);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task scheduler_fires_timeout_and_cancels_the_process()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(
            id, Guid.NewGuid(), 10m,
            PaymentTimeoutWindow: TimeSpan.FromSeconds(1)));

        // Give the scheduler a window to pick up the delayed message and for the
        // PaymentTimeoutHandler to run through FetchForWriting -> Apply -> SaveChangesAsync.
        await WaitForCondition(id, state => state.IsTerminal);

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        var cancelled = events[1].Data.ShouldBeOfType<OrderFulfillmentCancelled>();
        cancelled.Reason.ShouldBe("Payment timed out");

        var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
        state.ShouldNotBeNull();
        state.IsCancelled.ShouldBeTrue();
    }

    [Fact]
    public async Task payment_before_timeout_silences_the_timer()
    {
        var id = Guid.NewGuid();

        await Host.InvokeMessageAndWaitAsync(new StartOrderFulfillment(
            id, Guid.NewGuid(), 10m,
            PaymentTimeoutWindow: TimeSpan.FromSeconds(1)));

        // Payment arrives before the scheduled timeout fires.
        await Host.InvokeMessageAndWaitAsync(new PaymentConfirmed(id, 10m));

        // Wait out the scheduler window. The timeout handler will run, observe
        // state.PaymentConfirmed == true, and return empty Events.
        await Task.Delay(SchedulerObservationWindow);

        await using var session = Store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(id);

        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<OrderFulfillmentStarted>();
        events[1].Data.ShouldBeOfType<PaymentConfirmed>();

        var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
        state.ShouldNotBeNull();
        state.IsCancelled.ShouldBeFalse();
        state.PaymentConfirmed.ShouldBeTrue();
    }

    private async Task WaitForCondition(Guid id, Func<OrderFulfillmentState, bool> predicate)
    {
        var deadline = DateTime.UtcNow + SchedulerObservationWindow;
        while (DateTime.UtcNow < deadline)
        {
            await using var session = Store.LightweightSession();
            var state = await session.Events.FetchLatest<OrderFulfillmentState>(id);
            if (state is not null && predicate(state)) return;

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            $"Condition on OrderFulfillmentState {id} not met within {SchedulerObservationWindow}.");
    }
}
