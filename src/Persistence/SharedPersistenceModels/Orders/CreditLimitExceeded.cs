namespace SharedPersistenceModels.Orders;

public record CreditLimitExceeded(string OrderId, string CustomerId);