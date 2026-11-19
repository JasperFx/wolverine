using Bobcat;
using SagaWorkflows.OrderApproval;
using SagaWorkflows.Subscriptions;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.Util;

namespace SagaWorkflows.Specs;

// GH-5674, part 1: a message that arrives after its saga is gone is an UnknownSagaException, unless the saga has a
// NotFound for it. [DiscardIfSagaMissing] -- on the message type or on the saga's Handle method for it -- and
// [SagaMissing(Discard)] on the saga type log such a message and drop it instead. NotFound always wins, and the default
// is unchanged.

[BobcatFeature("An answer that arrives after the order is closed")]
[Collection(nameof(DocumentSagaCollection))]
public class answers_that_arrive_after_the_order_is_closed(DocumentSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _orderId = Guid.NewGuid();

    // The customer cancelled the order before the manager answered
    private async Task AnOrderTheCustomerCancelled()
    {
        await GivenReceived(new PlaceOrder(_orderId, "forklift", 80_000m));
        await GivenReceived(new CancelOrder(_orderId));
    }

    [BobcatSpec(typeof(ManagerDecision))]
    public async Task a_late_approval_is_dropped_through_the_attribute_on_its_Handle_method()
    {
        await AnOrderTheCustomerCancelled();

        await WhenReceived(new ManagerDecision(_orderId, Approved: true, "pat"));

        ThenHandled<ManagerDecision>();
        ThenNotDeadLettered<ManagerDecision>();
        ThenNoMessageSent<ProcessPayment>();
    }

    [BobcatSpec(typeof(ManagerComment))]
    public async Task a_late_comment_is_dropped_through_the_attribute_on_its_message_type()
    {
        await AnOrderTheCustomerCancelled();

        await WhenReceived(new ManagerComment(_orderId, "Looks fine to me"));

        ThenHandled<ManagerComment>();
        ThenNotDeadLettered<ManagerComment>();
    }

    // CancelOrder's Handle method is marked [DiscardIfSagaMissing] too, but the saga's NotFound answers it
    [BobcatSpec(typeof(CancelOrder))]
    public async Task a_late_cancellation_still_goes_to_NotFound()
    {
        await AnOrderTheCustomerCancelled();

        await WhenReceived(new CancelOrder(_orderId));

        ThenMessageSent(new NotifyCustomer(_orderId, $"Order {_orderId} had already closed"));
        ThenNotDeadLettered<CancelOrder>();
    }

    // Nothing marks PaymentProcessed: a payment for an order nobody knows is still a failure worth seeing
    [BobcatSpec(typeof(PaymentProcessed))]
    public async Task a_message_with_no_policy_for_a_missing_saga_still_fails()
    {
        await WhenReceived(new PaymentProcessed(_orderId));

        ThenDeadLettered<PaymentProcessed, UnknownSagaException>();
    }

    [BobcatSpec(typeof(ManagerDecision))]
    public async Task an_approval_while_the_order_waits_takes_the_payment()
    {
        await GivenReceived(new PlaceOrder(_orderId, "forklift", 80_000m));

        await WhenReceived(new ManagerDecision(_orderId, Approved: true, "pat"));

        ThenMessageSent(new ProcessPayment(_orderId, 80_000m));
        ThenMessageSent(new NotifyCustomer(_orderId, $"Order {_orderId} has completed"));
    }
}

[BobcatFeature("A signal for a subscription that has ended")]
[Collection(nameof(DocumentSagaCollection))]
public class signals_for_a_subscription_that_has_ended(DocumentSagaServices services) : SagaWorkflowSpec(services)
{
    private readonly Guid _subscriptionId = Guid.NewGuid();

    // [SagaMissing(Discard)] on the Subscription saga covers every message it handles
    [BobcatSpec(typeof(ChangePlan))]
    public async Task a_plan_change_after_the_subscription_was_cancelled_is_dropped()
    {
        await GivenReceived(new StartSubscription(_subscriptionId, "c-1", 20m, TimeSpan.FromDays(30), 12));
        await GivenReceived(new CancelSubscription(_subscriptionId));

        await WhenReceived(new ChangePlan(_subscriptionId, 30m, TimeSpan.FromDays(30)));

        ThenHandled<ChangePlan>();
        ThenNotDeadLettered<ChangePlan>();
        ThenNoMessageScheduled<BillingPeriodEnded>();
    }
}

[BobcatFeature("A signal for an event-sourced subscription that has ended")]
[Collection(nameof(EventSourcedSagaCollection))]
public class signals_for_an_event_sourced_subscription_that_has_ended(EventSourcedSagaServices services)
    : SagaWorkflowSpec(services)
{
    private readonly Guid _subscriptionId = Guid.NewGuid();

    // A completed event-sourced saga keeps its stream; a late message for it is treated as one for a missing saga
    [BobcatSpec(typeof(ChangePlan))]
    public async Task a_plan_change_after_the_subscription_was_cancelled_is_dropped()
    {
        await GivenReceived(new StartSubscription(_subscriptionId, "c-1", 20m, TimeSpan.FromDays(30), 12));
        await GivenReceived(new CancelSubscription(_subscriptionId));

        await WhenReceived(new ChangePlan(_subscriptionId, 30m, TimeSpan.FromDays(30)));

        ThenHandled<ChangePlan>();
        ThenNotDeadLettered<ChangePlan>();
        ThenNoEvents();
        await ThenTheSagaIsCompleted<EventSourcedSubscription>(_subscriptionId);
    }
}

// GH-5674, part 2: a plan change starts a new billing period, so the billing timeout already pending for the old one
// must never charge the customer. BillingPeriodEnded is marked [ReplacesEarlierTimeouts]: Wolverine keeps the latest
// timeout id with the saga, discards a superseded timeout before Handle runs, and deletes its scheduled message.

// Run on document and event-sourced subscriptions by the two classes below
public abstract class replacing_the_pending_billing_timeout(WorkflowServices services) : SagaWorkflowSpec(services)
{
    protected readonly Guid SubscriptionId = Guid.NewGuid();

    private StartSubscription Subscribe => new(SubscriptionId, "c-1", 20m, TimeSpan.FromDays(30), 12);

    private ChangePlan Upgrade => new(SubscriptionId, 30m, TimeSpan.FromDays(7));

    // Both billing timeouts are executed in the order they were scheduled: the first, superseded, never reaches Handle
    [BobcatSpec(typeof(BillingPeriodEnded))]
    public async Task the_superseded_timeout_never_charges_the_customer()
    {
        await WhenTracked("the customer subscribes and upgrades straight away", async bus =>
        {
            await bus.InvokeAsync(Subscribe);
            await bus.InvokeAsync(Upgrade);
        });
        await WhenScheduledMessagesAreExecuted();

        ThenMessageSent(new ChargeCustomer(SubscriptionId, "c-1", 1, 30m));
        ThenSentExactly(typeof(ChargeCustomer), 1);
    }

    [BobcatSpec(typeof(ChangePlan))]
    public async Task the_superseded_timeout_is_deleted_from_the_schedule()
    {
        await GivenReceived(Subscribe);

        await WhenReceived(Upgrade);

        ThenMessageScheduled<BillingPeriodEnded>();
        await ThenWaitingInTheSchedule(typeof(BillingPeriodEnded), SubscriptionId, 1);
    }

    [BobcatSpec(typeof(BillingPeriodEnded))]
    public async Task a_billing_timeout_nothing_replaced_charges_the_customer()
    {
        await WhenReceived(Subscribe);
        await WhenScheduledMessagesAreExecuted();

        ThenMessageSent(new ChargeCustomer(SubscriptionId, "c-1", 1, 20m));
        ThenMessageScheduled<BillingPeriodEnded>();
    }
}

[BobcatFeature("A plan change replaces the pending billing timeout")]
[Collection(nameof(DocumentSagaCollection))]
public class a_plan_change_replaces_the_pending_billing_timeout(DocumentSagaServices services)
    : replacing_the_pending_billing_timeout(services);

[BobcatFeature("A plan change replaces the pending billing timeout of an event-sourced subscription")]
[Collection(nameof(EventSourcedSagaCollection))]
public class a_plan_change_replaces_the_pending_billing_timeout_of_an_event_sourced_subscription(EventSourcedSagaServices services)
    : replacing_the_pending_billing_timeout(services)
{
    // Wolverine's own event records each timeout the subscription scheduled, keyed by the timeout's message type name
    [BobcatSpec(typeof(ChangePlan))]
    public async Task each_billing_timeout_is_recorded_on_the_stream()
    {
        await GivenReceived(new StartSubscription(SubscriptionId, "c-1", 20m, TimeSpan.FromDays(30), 12));

        await WhenReceived(new ChangePlan(SubscriptionId, 30m, TimeSpan.FromDays(7)));

        ThenEvents(new PlanChanged(30m, TimeSpan.FromDays(7)), Specify<SagaTimeoutScheduled>()
            .With(x => x.Key, typeof(BillingPeriodEnded).ToMessageTypeName()));
    }
}
