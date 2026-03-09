using PingPongContracts;
using Microsoft.Extensions.Logging;

namespace GrpcPonger;

/// <summary>
/// Wolverine handler that processes incoming Ping messages and produces Pong replies.
/// This handler is invoked by the gRPC endpoint adapter.
/// </summary>
public static class PingHandler
{
    public static PongMessage Handle(PingMessage ping, ILogger logger)
    {
        logger.LogInformation("Received Ping #{Number}: {Message}", ping.Number, ping.Message);

        return new PongMessage
        {
            Number = ping.Number,
            Message = $"Pong #{ping.Number}"
        };
    }
}
