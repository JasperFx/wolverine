using EventSourcedSagas.Fulfillment;
using JasperFx.Events;
using Wolverine;

namespace EventSourcedSagas.Returns;

// GH-5415: the same kind of saga written with one Evolve() method instead of Apply methods. Wolverine's own events --
// SagaCompleted, and the compensation events -- still reach the EventSourcedSaga base type's Apply methods
// (JasperFx/jasperfx#1241: an inherited Apply runs first, then the type's own Evolve if it names that event type).

public record RequestReturn(Guid ReturnRequestId, Guid OrderFulfillmentId, decimal Amount);

public record ReceiveReturnedItems(Guid ReturnRequestId);

public record RefundReturn(Guid ReturnRequestId);

public record ReturnRequested(Guid OrderFulfillmentId, decimal Amount);

public record ReturnedItemsReceived;

public record ReturnRefunded(decimal Amount);

public record ReturnRefundAbandoned(string Reason);

#region sample_event_sourced_saga_with_evolve
public class ReturnRequest : EventSourcedSaga
{
    public Guid Id { get; set; }
    public Guid OrderFulfillmentId { get; set; }
    public decimal Amount { get; set; }
    public bool ItemsReceived { get; set; }
    public bool IsRefunded { get; set; }
    public string? AbandonedBecause { get; set; }

    public static ReturnRequested Start(RequestReturn request) => new(request.OrderFulfillmentId, request.Amount);

    public ReturnedItemsReceived Handle(ReceiveReturnedItems received) => new();

    // Refunding the return ends it
    public IEnumerable<object> Handle(RefundReturn refund, PaymentLedger ledger)
    {
        ledger.Record(Id, Amount);
        return [new ReturnRefunded(Amount), new SagaCompleted()];
    }

    public ReturnRefundAbandoned Compensate(RefundReturn failed, LedgerUnavailableException ex)
        => new("The payment ledger was unavailable");

    public void Evolve(IEvent e)
    {
        switch (e.Data)
        {
            case ReturnRequested requested:
                Id = e.StreamId;
                OrderFulfillmentId = requested.OrderFulfillmentId;
                Amount = requested.Amount;
                break;
            case ReturnedItemsReceived _:
                ItemsReceived = true;
                break;
            case ReturnRefunded _:
                IsRefunded = true;
                break;
            case ReturnRefundAbandoned abandoned:
                AbandonedBecause = abandoned.Reason;
                break;
        }
    }
}
#endregion
