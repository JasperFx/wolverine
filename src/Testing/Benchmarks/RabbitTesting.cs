namespace Benchmarks
{
    public static class RabbitTesting
    {
        public static int Number = 0;

        public static string NextQueueName() => $"perf{++Number}";
        public static string NextExchangeName() => $"perf{++Number}";
    }
}