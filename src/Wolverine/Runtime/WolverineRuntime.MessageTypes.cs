namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    public void RegisterMessageType(Type messageType)
    {
        Handlers.RegisterMessageType(messageType);
    }
}