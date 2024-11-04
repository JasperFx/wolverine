namespace Messages;

public record OrderPlaced(string OrderId, string CustomerId, decimal Amount);