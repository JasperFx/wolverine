namespace Messages;

public record ReserveCredit(string OrderId, string CustomerId, decimal Amount);