namespace AspireWithKafka;

public static class OrderHandler
{
    public static void Handle(OrderPlaced order, ILogger logger)
    {
        logger.LogInformation(
            "Processing order {OrderId}: {Quantity}x {ProductCode}",
            order.OrderId, order.Quantity, order.ProductCode);
    }
}
