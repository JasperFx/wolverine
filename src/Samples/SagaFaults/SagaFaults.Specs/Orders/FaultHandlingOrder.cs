using SagaFaults.Inventory;
using Wolverine;

namespace SagaFaults.Orders.FaultHandling;

// Chapter 1 (GH-5553, with GH-4484 folded in): the Order saga hears about a step that failed, in another service or
// in its own.
#region sample_saga_that_hears_about_its_failed_steps
public class Order : Saga
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Amount { get; set; }

    public bool InventoryReserved { get; set; }
    public bool Paid { get; set; }

    public string? FailedStep { get; set; }
    public string? FailureReason { get; set; }

    public static (Order, ReserveInventory, ChargeCard) Start(PlaceOrder command)
        => (new Order { Id = command.OrderId, Sku = command.Sku, Quantity = command.Quantity, Amount = command.Amount },
            new ReserveInventory(command.Sku, command.Quantity),
            new ChargeCard(command.Amount));

    public void Handle(InventoryReserved reserved) => InventoryReserved = true;

    public void Handle(CardCharged charged) => Paid = true;

    // Declaring this is what has Wolverine ask for the fault of every ReserveInventory this saga sends: the
    // wolverine.fault.reply-to header, worked out when the application starts. No routing rule for
    // Fault<ReserveInventory> anywhere, and fault events can stay off in the inventory service.
    public void Handle(Fault<ReserveInventory> fault)
    {
        FailedStep = nameof(ReserveInventory);
        FailureReason = fault.Exception.Message;
    }

    // The same for a step in this service
    public void Handle(Fault<ChargeCard> fault)
    {
        FailedStep = nameof(ChargeCard);
        FailureReason = fault.Exception.Message;
    }

    // A Fault<ReserveInventory> with no saga id -- the ReserveInventory was not sent by an Order -- comes here
    // instead of failing with IndeterminateSagaStateIdException
    public static UnmatchedInventoryFailure IdNotProvided(Fault<ReserveInventory> fault) => new(fault.Message.Sku);

    // IdNotProvided is not only for faults: any message that reaches the saga with no saga id comes to the one declared
    // for its type. A card payment confirmed outside any order
    public static UnmatchedPayment IdNotProvided(CardCharged charged) => new(charged.Amount);
}
#endregion
