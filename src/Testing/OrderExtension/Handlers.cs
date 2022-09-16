using Wolverine.Attributes;

[assembly: WolverineModule]

namespace OrderExtension;

public class CreateOrder
{
}

public class ShipOrder{}

public class OrderHandler
{
    public void Handle(CreateOrder create){}

    public void Handle(ShipOrder command){}
}
