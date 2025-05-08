namespace SharedPersistenceModels.Orders;

public record OrderPlaced(string OrderId, string CustomerId, decimal Amount);