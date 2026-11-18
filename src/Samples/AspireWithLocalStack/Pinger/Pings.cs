namespace AspireWithLocalStack;

/// <summary>
/// Sent to an SQS queue
/// </summary>
public record Ping(Guid Id);

/// <summary>
/// Published to an SNS topic, which delivers it to an SQS queue this service listens to
/// </summary>
public record Pong(Guid Id);

/// <summary>
/// How many pongs this service has handled, which GET /pings reports
/// </summary>
public class PingTally
{
    private int _count;

    public int Count => _count;

    public void Increment() => Interlocked.Increment(ref _count);
}

public static class PingHandler
{
    // The Pong cascades out through SNS
    public static Pong Handle(Ping ping, ILogger logger)
    {
        logger.LogInformation("Received ping {Id} through SQS", ping.Id);
        return new Pong(ping.Id);
    }
}

public static class PongHandler
{
    public static void Handle(Pong pong, PingTally tally, ILogger logger)
    {
        tally.Increment();
        logger.LogInformation("Received pong {Id} through SNS", pong.Id);
    }
}
