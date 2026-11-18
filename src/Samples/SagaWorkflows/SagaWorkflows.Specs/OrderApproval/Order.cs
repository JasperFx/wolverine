using JasperFx;
using JasperFx.Core;
using Wolverine;
using Wolverine.Persistence.Sagas;

namespace SagaWorkflows.OrderApproval;

// GH-5674: the order approval of Dapr's .NET workflow sample (dapr/dotnet-sdk, OrderProcessingWorkflow). A large order
// waits for a manager's answer or a timeout. Answers are signals a person sends, so they can arrive after the order is
// closed -- cancelled by the customer, timed out, or already answered. A TimeoutMessage for a closed saga is dropped
// quietly; [DiscardIfSagaMissing] gives a signal the same treatment, where it says so. Each signal below shows one
// placement of the policy.

public record PlaceOrder([property: SagaIdentity] Guid OrderId, string Item, decimal TotalCost);

public record RequestApproval([property: SagaIdentity] Guid OrderId, decimal TotalCost);

// Nobody answered in time
public record ApprovalTimedOut([property: SagaIdentity] Guid OrderId) : TimeoutMessage(2.Days());

// A manager answered. [DiscardIfSagaMissing] is on the saga's Handle method for it, below
public record ManagerDecision([property: SagaIdentity] Guid OrderId, bool Approved, string Manager);

// A manager's note on the order, marked on the message type itself: late notes are dropped wherever they go
[DiscardIfSagaMissing]
public record ManagerComment([property: SagaIdentity] Guid OrderId, string Text);

// The customer cancels. The saga has a NotFound for it, which wins over [DiscardIfSagaMissing]
public record CancelOrder([property: SagaIdentity] Guid OrderId);

public record ProcessPayment([property: SagaIdentity] Guid OrderId, decimal Amount);

public record PaymentProcessed([property: SagaIdentity] Guid OrderId);

public record NotifyCustomer([property: SagaIdentity] Guid OrderId, string Text);

#region sample_discard_if_saga_missing
public class Order : Saga, IRevisioned
{
    public const decimal ApprovalThreshold = 50_000m;

    public Guid Id { get; set; }
    public string Item { get; set; } = "";
    public decimal TotalCost { get; set; }
    public string Stage { get; set; } = "Received";
    public List<string> Comments { get; set; } = [];

    public static (Order, OutgoingMessages) Start(PlaceOrder command)
    {
        var order = new Order { Id = command.OrderId, Item = command.Item, TotalCost = command.TotalCost };
        if (command.TotalCost <= ApprovalThreshold) return (order, order.takePayment());

        order.Stage = "Waiting for approval";
        return (order, [new RequestApproval(order.Id, order.TotalCost), new ApprovalTimedOut(order.Id)]);
    }

    // An answer for an order that is already closed is logged and dropped, not an UnknownSagaException
    [DiscardIfSagaMissing]
    public OutgoingMessages Handle(ManagerDecision decision)
        => decision.Approved ? takePayment() : finish($"Order {Id} was rejected by {decision.Manager}");

    public void Handle(ManagerComment comment) => Comments.Add(comment.Text);

    public OutgoingMessages Handle(ApprovalTimedOut timedOut)
        => Stage == "Waiting for approval" ? finish($"Order {Id} was cancelled: nobody approved it in time") : [];

    public OutgoingMessages Handle(PaymentProcessed processed) => finish($"Order {Id} has completed");

    [DiscardIfSagaMissing]
    public OutgoingMessages Handle(CancelOrder cancel) => finish($"Order {Id} was cancelled");

    // NotFound always wins: the customer is told the order had already closed
    public static NotifyCustomer NotFound(CancelOrder cancel)
        => new(cancel.OrderId, $"Order {cancel.OrderId} had already closed");

    private OutgoingMessages takePayment()
    {
        Stage = "Processing payment";
        return [new ProcessPayment(Id, TotalCost)];
    }

    private OutgoingMessages finish(string text)
    {
        Stage = "Closed";
        MarkCompleted();
        return [new NotifyCustomer(Id, text)];
    }
}
#endregion
