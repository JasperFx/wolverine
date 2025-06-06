namespace ItemService.Orders;

public record ReserveCredit(string OrderId, string CustomerId, decimal Amount);