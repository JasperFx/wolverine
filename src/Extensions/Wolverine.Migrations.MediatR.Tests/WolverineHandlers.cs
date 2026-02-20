using Wolverine.Attributes;

namespace Wolverine.Migrations.MediatR.Tests;

/// <summary>
/// Wolverine handler that receives cascading messages from MediatR handlers
/// </summary>

public static class CascadingMessageHandler
{
    public static string? ReceivedData { get; set; }

    [WolverineHandler]
    public static void Handle(CascadingMessage message)
    {
        ReceivedData = message.Data;
    }
}
