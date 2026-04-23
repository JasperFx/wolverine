namespace ProcessManagerSample.OrderFulfillment;

/// <summary>
/// Event-sourced state for the order fulfillment process. Projected inline from the event stream
/// via Apply methods. Serves as the correlation surface for the handlers that coordinate payment,
/// warehouse, and shipping steps.
/// </summary>
public class OrderFulfillmentState
{
    // Required by Marten: FetchForWriting registers the aggregate type as a document type.
    // Without a public Guid Id { get; set; }, CleanAllDataAsync throws InvalidDocumentException.
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    public bool PaymentConfirmed { get; set; }
    public bool ItemsReserved { get; set; }
    public bool ShipmentConfirmed { get; set; }

    public bool IsCompleted { get; set; }
    public bool IsCancelled { get; set; }

    /// <summary>
    /// True once the process has reached a terminal state. Every continue handler must guard on this
    /// to stay idempotent against late-arriving messages after completion or cancellation.
    /// </summary>
    public bool IsTerminal => IsCompleted || IsCancelled;

    public void Apply(OrderFulfillmentStarted e)
    {
        Id = e.OrderFulfillmentStateId;
        CustomerId = e.CustomerId;
        TotalAmount = e.TotalAmount;
    }

    public void Apply(PaymentConfirmed _) => PaymentConfirmed = true;

    public void Apply(ItemsReserved _) => ItemsReserved = true;

    public void Apply(ShipmentConfirmed _) => ShipmentConfirmed = true;

    public void Apply(OrderFulfillmentCompleted _) => IsCompleted = true;

    public void Apply(OrderFulfillmentCancelled _) => IsCancelled = true;
}
