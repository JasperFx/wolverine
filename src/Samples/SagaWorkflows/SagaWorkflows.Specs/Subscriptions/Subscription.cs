using JasperFx;
using Wolverine;

namespace SagaWorkflows.Subscriptions;

// GH-5674: the subscription as a document saga. A signal that arrives after the subscription ended -- cancelled, or out
// of billing periods -- is dropped: [SagaMissing(Discard)] on the saga type covers every message it handles.
#region sample_subscription_saga
[SagaMissing(SagaMissingBehavior.Discard)]
public class Subscription : Saga, IRevisioned
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";
    public decimal Charge { get; set; }
    public TimeSpan BillingPeriod { get; set; }
    public int MaxBillingPeriods { get; set; }

    public int BillingPeriodNumber { get; set; } = 1;
    public decimal TotalCharged { get; set; }

    public static (Subscription, BillingPeriodEnded) Start(StartSubscription command)
        => (new Subscription
            {
                Id = command.SubscriptionId,
                CustomerId = command.CustomerId,
                Charge = command.Charge,
                BillingPeriod = command.BillingPeriod,
                MaxBillingPeriods = command.MaxBillingPeriods
            },
            new BillingPeriodEnded(command.SubscriptionId, 1, command.BillingPeriod));

    // No "is this the timeout for the current period?" guard: a timeout a later one replaced never gets here
    public OutgoingMessages Handle(BillingPeriodEnded ended)
    {
        var charge = new ChargeCustomer(Id, CustomerId, BillingPeriodNumber, Charge);
        TotalCharged += Charge;
        BillingPeriodNumber++;

        if (BillingPeriodNumber > MaxBillingPeriods)
        {
            MarkCompleted();
            return [charge, new SubscriptionEnded(Id, CustomerId, "All billing periods were charged")];
        }

        return [charge, new BillingPeriodEnded(Id, BillingPeriodNumber, BillingPeriod)];
    }

    // The new plan's first billing period starts now. The BillingPeriodEnded this returns replaces the pending one
    public BillingPeriodEnded Handle(ChangePlan change)
    {
        Charge = change.Charge;
        BillingPeriod = change.BillingPeriod;
        return new BillingPeriodEnded(Id, BillingPeriodNumber, BillingPeriod);
    }

    public SubscriptionEnded Handle(CancelSubscription cancel)
    {
        MarkCompleted();
        return new SubscriptionEnded(Id, CustomerId, "Cancelled by the customer");
    }
}
#endregion
