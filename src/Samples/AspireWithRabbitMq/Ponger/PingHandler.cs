namespace AspireWithRabbitMq;

public static class PingHandler
{
    public static PongMessage Handle(PingMessage message, ILogger logger)
    {
        logger.LogInformation("Got ping #{Number}, sending pong", message.Number);
        return new PongMessage(message.Number);
    }
}
