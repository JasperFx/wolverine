namespace Wolverine.Kafka.Tests;

public static class KafkaContainerFixture
{
    // Uses docker-compose Kafka on localhost:9092
    // Start with: docker compose up -d kafka
    public static string ConnectionString => "localhost:9092";
}
