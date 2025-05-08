namespace SharedPersistenceModels.Orders;

public record PlaceOrder(
    string OrderId,
    string CustomerId,
    decimal Amount
);