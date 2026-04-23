using ProcessManagerSample.OrderFulfillment;
using ProcessManagerSample.OrderFulfillment.Handlers;
using Shouldly;
using Xunit;

namespace ProcessManagerSample.Tests.OrderFulfillment;

/// <summary>
/// Pure function tests. No Wolverine host, no Marten session. The handlers are static methods
/// over plain inputs, so we can construct state in memory and assert directly on the returned events.
/// </summary>
public class HandlerUnitTests
{
    private static OrderFulfillmentState InProgressState(
        bool paymentConfirmed = false,
        bool itemsReserved = false,
        bool shipmentConfirmed = false)
    {
        return new OrderFulfillmentState
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            TotalAmount = 100m,
            PaymentConfirmed = paymentConfirmed,
            ItemsReserved = itemsReserved,
            ShipmentConfirmed = shipmentConfirmed
        };
    }

    [Fact]
    public void payment_confirmed_records_event_and_stays_in_progress_when_gates_remain()
    {
        var state = InProgressState();
        var @event = new PaymentConfirmed(state.Id, state.TotalAmount);

        var result = PaymentConfirmedHandler.Handle(@event, state);

        result.Count.ShouldBe(1);
        result[0].ShouldBeOfType<PaymentConfirmed>();
    }

    [Fact]
    public void payment_confirmed_also_completes_when_other_two_gates_are_already_satisfied()
    {
        var state = InProgressState(itemsReserved: true, shipmentConfirmed: true);
        var @event = new PaymentConfirmed(state.Id, state.TotalAmount);

        var result = PaymentConfirmedHandler.Handle(@event, state);

        result.Count.ShouldBe(2);
        result[0].ShouldBeOfType<PaymentConfirmed>();
        var completed = result[1].ShouldBeOfType<OrderFulfillmentCompleted>();
        completed.OrderFulfillmentStateId.ShouldBe(state.Id);
    }

    [Fact]
    public void payment_confirmed_is_a_no_op_when_payment_already_recorded()
    {
        var state = InProgressState(paymentConfirmed: true);
        var @event = new PaymentConfirmed(state.Id, state.TotalAmount);

        var result = PaymentConfirmedHandler.Handle(@event, state);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void completion_guard_rejects_a_continue_message_after_terminal_state()
    {
        var state = InProgressState();
        state.IsCompleted = true;

        var result = PaymentConfirmedHandler.Handle(
            new PaymentConfirmed(state.Id, state.TotalAmount),
            state);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void completion_guard_rejects_a_continue_message_after_cancellation()
    {
        var state = InProgressState();
        state.IsCancelled = true;

        var result = ShipmentConfirmedHandler.Handle(
            new ShipmentConfirmed(state.Id, "TRACK-1"),
            state);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void cancel_emits_terminal_event_with_reason()
    {
        var state = InProgressState(paymentConfirmed: true);
        var command = new CancelOrderFulfillment(state.Id, "Customer requested");

        var result = CancelOrderFulfillmentHandler.Handle(command, state);

        result.Count.ShouldBe(1);
        var cancelled = result[0].ShouldBeOfType<OrderFulfillmentCancelled>();
        cancelled.OrderFulfillmentStateId.ShouldBe(state.Id);
        cancelled.Reason.ShouldBe("Customer requested");
    }

    [Fact]
    public void cancel_on_already_terminal_state_is_a_no_op()
    {
        var state = InProgressState();
        state.IsCompleted = true;

        var result = CancelOrderFulfillmentHandler.Handle(
            new CancelOrderFulfillment(state.Id, "Too late"),
            state);

        result.ShouldBeEmpty();
    }
}
