namespace KafkaPerfRig;

/// <summary>
/// All rig knobs come from environment variables so rig.sh can define a scenario as a
/// one-line env prefix. Every value has a default that produces the GH-3490 client-shaped
/// baseline when run as-is.
/// </summary>
public class RigConfig
{
    public string BootstrapServers { get; } = env("RIG_BOOTSTRAP", "localhost:9092");
    public string RunId { get; } = env("RIG_RUN_ID", "dev");

    // Topics are suffixed with the run id so every run starts on fresh topics and
    // AutoOffsetReset=Earliest can never replay a previous run's records.
    public string SmallTopic => $"rig.small.{RunId}";
    public string LargeTopic => $"rig.large.{RunId}";

    public int Partitions { get; } = envInt("RIG_PARTITIONS", 12);

    // Client shape: 1Kb flow at ~8/s, 100Kb flow at ~0.6/s
    public double SmallRate { get; } = envDouble("RIG_SMALL_RATE", 8.0);
    public double LargeRate { get; } = envDouble("RIG_LARGE_RATE", 0.6);
    public int Games { get; } = envInt("RIG_GAMES", 20);

    public int WarmupSeconds { get; } = envInt("RIG_WARMUP_S", 30);
    public int DurationSeconds { get; } = envInt("RIG_DURATION_S", 120);

    // wolverine consumer endpoint mode: buffered | durable | inline
    public string ConsumerMode { get; } = env("RIG_MODE", "buffered");

    // wolverine publisher sending: batched | inline
    public string SendMode { get; } = env("RIG_SEND_MODE", "batched");
    public int BatchSize { get; } = envInt("RIG_BATCH_SIZE", 10);
    public int BatchTimeoutMs { get; } = envInt("RIG_BATCH_TIMEOUT_MS", 10);

    // simulated handler work in ms (client's native handler p50 was ~9ms); 0 = no-op handler
    public int HandlerMs { get; } = envInt("RIG_HANDLER_MS", 9);

    // sequencing shape inside the consumer: none | semaphore (client-shaped per-game SemaphoreSlim)
    public string Sequencing { get; } = env("RIG_SEQ", "semaphore");

    // 0 = leave Wolverine's default (max(ProcessorCount, 5))
    public int MaxParallel { get; } = envInt("RIG_MAX_PARALLEL", 0);

    public string OutDir { get; } = env("RIG_OUT", Path.Combine("rig-results", env("RIG_RUN_ID", "dev")));

    public string PostgresSchema { get; } = env("RIG_PG_SCHEMA", "kafka_rig");

    // 0 = leave the endpoint's default listener count
    public int ListenerCount { get; } = envInt("RIG_LISTENER_COUNT", 0);

    // --- RabbitMQ (GH-3492) ---
    public string RabbitUri { get; } = env("RIG_RABBIT_URI", "amqp://guest:guest@localhost:5672");
    public string SmallQueue => $"rig-small-{RunId}";
    public string LargeQueue => $"rig-large-{RunId}";

    // native Rabbit twin prefetch; match Wolverine's listener PreFetchCount default of 100
    public ushort RabbitPrefetch { get; } = (ushort)envInt("RIG_RABBIT_PREFETCH", 100);

    private static string env(string key, string fallback)
    {
        return Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;
    }

    private static int envInt(string key, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
    }

    private static double envDouble(string key, double fallback)
    {
        return double.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
    }

    public string Describe()
    {
        return
            $"run={RunId} mode={ConsumerMode} send={SendMode} batch=({BatchSize},{BatchTimeoutMs}ms) " +
            $"seq={Sequencing} handler={HandlerMs}ms rates=({SmallRate}/s small, {LargeRate}/s large) " +
            $"warmup={WarmupSeconds}s duration={DurationSeconds}s partitions={Partitions}";
    }
}
