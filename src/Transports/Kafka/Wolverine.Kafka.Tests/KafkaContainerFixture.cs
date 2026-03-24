using System.Runtime.CompilerServices;
using Testcontainers.Kafka;

namespace Wolverine.Kafka.Tests;

public static class KafkaContainerFixture
{
    private static KafkaContainer? _container;

    public static string ConnectionString { get; private set; } = "localhost:9092";

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            _container = new KafkaBuilder()
                .WithImage("confluentinc/cp-kafka:7.6.1")
                .Build();

            _container.StartAsync().GetAwaiter().GetResult();
            ConnectionString = _container.GetBootstrapAddress();
        }
        catch
        {
            // Fall back to docker-compose Kafka on localhost:9092
        }
    }
}
