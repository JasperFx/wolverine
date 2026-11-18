using Bobcat;
using EventSourcedSagas.Fulfillment;

namespace EventSourcedSagas.Specs;

// GH-5415: the payment timeout, a TimeoutMessage, fired with WhenScheduledMessagesAreExecuted
// (ITrackedSession.PlayScheduledMessagesAsync) rather than by waiting for the clock.

[BobcatFeature("An event-sourced saga's timeout")]
[Collection(nameof(FulfillmentCollection))]
public class the_payment_timeout(InlineFulfillmentServices services) : EventSourcedSagaSpec(services)
{
    [BobcatSpec(typeof(PaymentTimeout), Lifecycle = Lifecycle.Acceptance)]
    public async Task an_unpaid_fulfillment_is_cancelled_when_its_payment_times_out()
    {
        await WhenReceived(new StartOrderFulfillment(OrderFulfillmentId, CustomerId, 100m));
        await WhenScheduledMessagesAreExecuted();

        ThenEvents(new OrderFulfillmentCancelled("Payment timed out"));
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
    }

    [BobcatSpec(typeof(PaymentTimeout), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_paid_fulfillment_ignores_its_payment_timeout()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid());

        await WhenReceived(new PaymentTimeout(OrderFulfillmentId));
        await WhenScheduledMessagesAreExecuted();

        ThenNoEvents();
        await ThenTheSagaIsStillOpen<OrderFulfillment>(OrderFulfillmentId);
    }

    [BobcatSpec(typeof(PaymentTimeout), Lifecycle = Lifecycle.Acceptance)]
    public async Task a_timeout_for_a_completed_fulfillment_is_ignored()
    {
        await GivenEvents<OrderFulfillment>(OrderFulfillmentId, Started(), Paid(), Reserved, Shipped, Completed);

        await WhenReceived(new PaymentTimeout(OrderFulfillmentId));
        await WhenScheduledMessagesAreExecuted();

        ThenNoEvents();
        ThenNotDeadLettered<PaymentTimeout>();
        await ThenTheSagaIsCompleted<OrderFulfillment>(OrderFulfillmentId);
    }
}
