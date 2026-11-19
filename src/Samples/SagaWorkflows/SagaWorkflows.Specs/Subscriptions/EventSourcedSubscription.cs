using Wolverine;

namespace SagaWorkflows.Subscriptions;

// GH-5674: the same subscription as an event-sourced saga. Wolverine keeps the latest billing timeout's id as an event
// of its own on the saga's stream (wolverine_saga_timeout_scheduled), so the history shows every reschedule.

public record SubscriptionStarted(Guid SubscriptionId, string CustomerId, decimal Charge, TimeSpan BillingPeriod,
    int MaxBillingPeriods);

public record CustomerCharged(int Period, decimal Amount);

public record PlanChanged(decimal Charge, TimeSpan BillingPeriod);

public record SubscriptionCancelled;

[SagaMissing(SagaMissingBehavior.Discard)]
public class EventSourcedSubscription : EventSourcedSaga
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";
    public decimal Charge { get; set; }
    public TimeSpan BillingPeriod { get; set; }
    public int MaxBillingPeriods { get; set; }

    public int BillingPeriodNumber { get; set; } = 1;
    public decimal TotalCharged { get; set; }

    public static (SubscriptionStarted, BillingPeriodEnded) Start(StartSubscription command)
        => (new SubscriptionStarted(command.SubscriptionId, command.CustomerId, command.Charge, command.BillingPeriod,
                command.MaxBillingPeriods),
            new BillingPeriodEnded(command.SubscriptionId, 1, command.BillingPeriod));

    // Events for the stream, and messages to send
    public (IEnumerable<object>, OutgoingMessages) Handle(BillingPeriodEnded ended)
    {
        var charged = new CustomerCharged(BillingPeriodNumber, Charge);
        var charge = new ChargeCustomer(Id, CustomerId, BillingPeriodNumber, Charge);

        return BillingPeriodNumber >= MaxBillingPeriods
            ? ([charged, new SagaCompleted()], [charge, new SubscriptionEnded(Id, CustomerId, "All billing periods were charged")])
            : ([charged], [charge, new BillingPeriodEnded(Id, BillingPeriodNumber + 1, BillingPeriod)]);
    }

    public (PlanChanged, BillingPeriodEnded) Handle(ChangePlan change)
        => (new PlanChanged(change.Charge, change.BillingPeriod),
            new BillingPeriodEnded(Id, BillingPeriodNumber, change.BillingPeriod));

    public (SubscriptionCancelled, SagaCompleted, OutgoingMessages) Handle(CancelSubscription cancel)
        => (new SubscriptionCancelled(), new SagaCompleted(),
            [new SubscriptionEnded(Id, CustomerId, "Cancelled by the customer")]);

    public void Apply(SubscriptionStarted e)
    {
        Id = e.SubscriptionId;
        CustomerId = e.CustomerId;
        Charge = e.Charge;
        BillingPeriod = e.BillingPeriod;
        MaxBillingPeriods = e.MaxBillingPeriods;
    }

    public void Apply(CustomerCharged e)
    {
        TotalCharged += e.Amount;
        BillingPeriodNumber = e.Period + 1;
    }

    public void Apply(PlanChanged e)
    {
        Charge = e.Charge;
        BillingPeriod = e.BillingPeriod;
    }
}
