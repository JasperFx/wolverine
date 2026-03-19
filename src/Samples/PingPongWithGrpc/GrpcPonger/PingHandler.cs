using GrpcPingPongContracts;
using Spectre.Console;

namespace GrpcPonger;

/// <summary>
/// Wolverine handler that processes incoming Ping messages and produces Pong replies.
/// This handler is invoked by the gRPC endpoint adapter.
/// </summary>
public static class PingHandler
{
    public static PongMessage Handle(PingMessage message)
    {
        AnsiConsole.MarkupLine($"[blue]Got ping #{message.Number}[/]");

        return new PongMessage { Number = message.Number, };
    }
}
