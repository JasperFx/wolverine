namespace ItemService.Orders;

public record CreditLimitExceeded(string OrderId, string CustomerId);