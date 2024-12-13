namespace WolverineWebApi;

public class TinyOrder
{
    public TinyOrder(int orderId)
    {
        OrderId = orderId;
    }

    public TinyOrder()
    {
    }

    public int OrderId { get; set; }
}

public record CreateOrder(int OrderId);