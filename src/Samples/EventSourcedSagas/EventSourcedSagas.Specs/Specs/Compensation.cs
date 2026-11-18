using Bobcat;
using EventSourcedSagas.Fulfillment;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace EventSourcedSagas.Specs;

// GH-5415 with GH-5556: an event-sourced saga's Compensate returns events and messages, as Handle does. Wolverine records
// the compensation as events of its own on the saga's stream -- SagaCompensating, then SagaCompensated or
// SagaCompensationFailed -- and the EventSourcedSaga base type rebuilds Status and CompensationLog from them.

[BobcatFeature("An event-sourced saga's compensation is recorded as events")]
[Collection(nameof(FulfillmentCollection))]
public class compensating_an_event_sourced_saga(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(PaymentReceived), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_payment_the_ledger_cannot_record_is_retried_then_refunded()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());
        LedgerIsUnavailable();

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 100m));

        ThenAttempted<PaymentReceived>(3);
        await ThenDeadLettered<PaymentReceived>(by: FulfillmentServices.ServiceName, tagged: "compensated");
        ThenEvents(typeof(SagaCompensating), typeof(RefundIssued), typeof(SagaCompensated));
        ThenMessageSent(new RefundPayment(OrderFulfillmentId, 100m));
        await ThenTheSagaStatusIs<OrderFulfillment>(OrderFulfillmentId, SagaStatus.Compensated);
        await ThenTheSagaCompensated<OrderFulfillment, PaymentReceived, LedgerUnavailableException>(OrderFulfillmentId);
    }

    // PaymentAmountMismatchException is marked [TerminalFailure]
    [BobcatSpec(typeof(PaymentReceived), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_wrong_payment_amount_is_refunded_at_once()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 90m));

        ThenAttempted<PaymentReceived>(1);
        ThenEvents(typeof(SagaCompensating), typeof(RefundIssued), typeof(SagaCompensated));
        ThenMessageSent(new RefundPayment(OrderFulfillmentId, 90m));
        await ThenTheSagaCompensated<OrderFulfillment, PaymentReceived, PaymentAmountMismatchException>(OrderFulfillmentId);
    }

    // The Compensate for a wrong amount throws RefundAlreadyIssuedException when a refund was already issued. The events
    // it would have returned are not appended; Wolverine's own two are, in a transaction of their own
    [BobcatSpec(typeof(PaymentReceived), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_compensation_that_fails_is_recorded_as_compensation_failed()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), new RefundIssued(100m));

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: FulfillmentServices.ServiceName, tagged: "compensation-failed");
        ThenEvents(typeof(SagaCompensating), typeof(SagaCompensationFailed));
        ThenNoMessageSent<RefundPayment>();
        await ThenTheSagaStatusIs<OrderFulfillment>(OrderFulfillmentId, SagaStatus.CompensationFailed);
        await ThenTheSagaFailedToCompensate<OrderFulfillment, PaymentReceived, PaymentAmountMismatchException>(OrderFulfillmentId);
    }
}
