using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaWorkflows.Subscriptions;

// GH-5674: the subscription of Temporal's subscription workflow tutorial: a charge every billing period, up to a
// maximum number of periods, with signals to change the plan and to cancel. The billing loop is one timeout that each
// period schedules for the next. Changing the plan starts a new billing period at once, so the timeout already pending
// for the old period must never fire: [ReplacesEarlierTimeouts] says so, where each saga used to compare the period in
// the timeout with its own.

public record StartSubscription(
    [property: SagaIdentity] Guid SubscriptionId,
    string CustomerId,
    decimal Charge,
    TimeSpan BillingPeriod,
    int MaxBillingPeriods);

// Signals
public record ChangePlan([property: SagaIdentity] Guid SubscriptionId, decimal Charge, TimeSpan BillingPeriod);

public record CancelSubscription([property: SagaIdentity] Guid SubscriptionId);

// Temporal's sleep(billingPeriod). A later BillingPeriodEnded for the same subscription replaces this one
#region sample_replaces_earlier_timeouts
[ReplacesEarlierTimeouts]
public record BillingPeriodEnded([property: SagaIdentity] Guid SubscriptionId, int Period, TimeSpan Delay)
    : TimeoutMessage(Delay);
#endregion

// Activities
public record ChargeCustomer([property: SagaIdentity] Guid SubscriptionId, string CustomerId, int Period, decimal Amount);

public record SubscriptionEnded([property: SagaIdentity] Guid SubscriptionId, string CustomerId, string Why);
