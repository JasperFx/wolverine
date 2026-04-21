using Wolverine.Attributes;

namespace AspireWithRabbitMq;

public static class PongHandler
{
    public static void Handle(PongMessage message, ILogger logger)
    {
        logger.LogInformation("Got pong #{Number}", message.Number);
    }
}
