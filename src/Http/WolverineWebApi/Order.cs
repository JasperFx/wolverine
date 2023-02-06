namespace WolverineWebApi;

public class Order
{
    public int OrderId { get; set; }

    public Order(int orderId)
    {
        OrderId = orderId;
    }

    public Order()
    {
    }
}

public record CreateOrder(int OrderId);