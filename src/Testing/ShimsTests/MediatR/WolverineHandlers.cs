using Wolverine.Attributes;

namespace Wolverine.Shims.Tests.MediatR;

public static class CascadingMessageHandler
{
    public static string? ReceivedData { get; set; }

    [WolverineHandler]
    public static void Handle(CascadingMessage message)
    {
        ReceivedData = message.Data;
    }
}
