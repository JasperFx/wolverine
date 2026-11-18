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
    // Wolverine's own events, for a failed PaymentReceived and the exception it failed with
    private Specified<SagaCompensating> Compensating<TException>() where TException : Exception
        => Specify<SagaCompensating>().With(x => x.ExceptionType, typeof(TException).FullName!);

    private Specified<SagaCompensated> Compensated<TException>() where TException : Exception
        => Specify<SagaCompensated>().With(x => x.ExceptionType, typeof(TException).FullName!);

    private Specified<SagaCompensationFailed> CompensationFailed<TException>() where TException : Exception
        => Specify<SagaCompensationFailed>().With(x => x.ExceptionType, typeof(TException).FullName!);

    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_payment_the_ledger_cannot_record_is_retried_then_refunded()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());
        LedgerIsUnavailable();

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 100m));

        ThenAttempted<PaymentReceived>(3);
        await ThenDeadLettered<PaymentReceived>(by: FulfillmentServices.ServiceName, tagged: "compensated");
        ThenCaused(Compensating<LedgerUnavailableException>(), new RefundIssued(100m), Compensated<LedgerUnavailableException>());
        ThenMessageSent(new RefundPayment(OrderFulfillmentId, 100m));
        await ThenTheSagaStatusIs<OrderFulfillment>(OrderFulfillmentId, SagaStatus.Compensated);
        await ThenTheSagaCompensated<OrderFulfillment, PaymentReceived, LedgerUnavailableException>(OrderFulfillmentId);
    }

    // PaymentAmountMismatchException is marked [TerminalFailure]
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_wrong_payment_amount_is_refunded_at_once()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 90m));

        ThenAttempted<PaymentReceived>(1);
        ThenCaused(Compensating<PaymentAmountMismatchException>(), new RefundIssued(90m), Compensated<PaymentAmountMismatchException>());
        ThenMessageSent(new RefundPayment(OrderFulfillmentId, 90m));
        await ThenTheSagaCompensated<OrderFulfillment, PaymentReceived, PaymentAmountMismatchException>(OrderFulfillmentId);
    }

    // The Compensate for a wrong amount throws RefundAlreadyIssuedException when a refund was already issued. The events
    // it would have returned are not appended; Wolverine's own two are, in a transaction of their own
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_compensation_that_fails_is_recorded_as_compensation_failed()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), new RefundIssued(100m));

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: FulfillmentServices.ServiceName, tagged: "compensation-failed");
        ThenCaused(Compensating<PaymentAmountMismatchException>(), CompensationFailed<PaymentAmountMismatchException>());
        ThenNoMessageSent<RefundPayment>();
        await ThenTheSagaStatusIs<OrderFulfillment>(OrderFulfillmentId, SagaStatus.CompensationFailed);
        await ThenTheSagaFailedToCompensate<OrderFulfillment, PaymentReceived, PaymentAmountMismatchException>(OrderFulfillmentId);
    }
}

[BobcatFeature("An event-sourced saga's compensation meets a concurrent append")]
[Collection(nameof(FulfillmentCollection))]
public class compensating_while_another_message_appends(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    // The compensation loads the stream, then another message appends to it before the compensation saves: the save is a
    // concurrency conflict, and the compensation is retried under its own error policies
    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_conflict_once_is_retried_and_the_compensation_succeeds()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());
        WhileCompensatingAnotherMessageAppends<OrderFulfillment>(OrderFulfillmentId, Reserved);

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: FulfillmentServices.ServiceName, tagged: "compensated");
        ThenMessageSent(new RefundPayment(OrderFulfillmentId, 90m));
        await ThenTheSagaStatusIs<OrderFulfillment>(OrderFulfillmentId, SagaStatus.Compensated);
        await ThenSaga<OrderFulfillment>(OrderFulfillmentId, Specify<OrderFulfillment>()
            .With(x => x.AreItemsReserved, true)
            .With(x => x.IsRefunded, true));
    }

    [BobcatSpec(typeof(PaymentReceived))]
    public async Task a_conflict_on_every_attempt_leaves_the_saga_compensation_failed()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started());
        WhileCompensatingAnotherMessageAppends<OrderFulfillment>(OrderFulfillmentId, Reserved, onEveryAttempt: true);

        await WhenReceived(new PaymentReceived(OrderFulfillmentId, 90m));

        await ThenDeadLettered<PaymentReceived>(by: FulfillmentServices.ServiceName, tagged: "compensation-failed");
        ThenNoMessageSent<RefundPayment>();
        await ThenTheSagaStatusIs<OrderFulfillment>(OrderFulfillmentId, SagaStatus.CompensationFailed);
        await ThenTheSagaFailedToCompensate<OrderFulfillment, PaymentReceived, PaymentAmountMismatchException>(OrderFulfillmentId);
    }
}
