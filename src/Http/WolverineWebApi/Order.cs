namespace WolverineWebApi;

public class Order
{
    public Order(int orderId)
    {
        OrderId = orderId;
    }

    public Order()
    {
    }

    public int OrderId { get; set; }
}

public record CreateOrder(int OrderId);