namespace Wolverine.SignalR;

public static class SignalRActions
{
    public static ISignalRAction RemoveFromGroup(string groupName) => new RemoveConnectionToGroup(groupName);

    public static ISignalRAction AddToGroup(string groupName) => new AddConnectionToGroup(groupName);

    public static ISignalRAction DoNothing => new DoNothing();
}