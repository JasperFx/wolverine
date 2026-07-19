namespace Benchmarks;

public static class RabbitTesting
{
    public static int Number;

    public static string NextQueueName()
    {
        return $"perf{++Number}";
    }

    public static string NextExchangeName()
    {
        return $"perf{++Number}";
    }
}