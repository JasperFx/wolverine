namespace Messages;

public record PlaceOrder(
    string OrderId,
    string CustomerId,
    decimal Amount
);