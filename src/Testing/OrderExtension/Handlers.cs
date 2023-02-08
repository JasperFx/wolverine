using Wolverine.Attributes;

[assembly: WolverineModule]

namespace OrderExtension;

public class CreateOrder
{
}

public class ShipOrder
{
}

public class OrderHandler
{
    public void HandleAsync(CreateOrder create)
    {
    }

    public void HandleAsync(ShipOrder command)
    {
    }
}