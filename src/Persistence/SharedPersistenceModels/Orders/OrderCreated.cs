namespace SharedPersistenceModels.Orders;

public record OrderCreated(string OrderId, string CustomerId, string CustomerName);