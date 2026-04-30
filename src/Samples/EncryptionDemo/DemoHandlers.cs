namespace EncryptionDemo;

public sealed record PaymentDetails(string CardNumber, decimal Amount);

public sealed record OrderShipped(Guid OrderId);

public static class PaymentDetailsHandler
{
    public static void Handle(PaymentDetails msg) =>
        Console.WriteLine($"Payment received (decrypted): {msg.Amount} on card ending {msg.CardNumber[^4..]}");
}

public static class OrderShippedHandler
{
    public static void Handle(OrderShipped msg) =>
        Console.WriteLine($"Order {msg.OrderId} shipped (plain JSON).");
}
