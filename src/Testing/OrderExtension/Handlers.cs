#region sample_using_wolverine_module_attribute

using Wolverine.Attributes;

[assembly: WolverineModule]

#endregion

namespace OrderExtension;

public class CreateOrder;

public class ShipOrder;

public class OrderHandler
{
    public void HandleAsync(CreateOrder create)
    {
    }

    public void HandleAsync(ShipOrder command)
    {
    }
}