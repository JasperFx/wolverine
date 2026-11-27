using System.Collections.Concurrent;
using Dekaf.Producer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using DekafClient = Dekaf.Kafka;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// How Wolverine builds its Dekaf clients:
/// GH-5999, each <c>Configure*Builders</c> call adds to the earlier ones instead of replacing them;
/// GH-6000, <c>KafkaProducerOptions.Partitioner</c>, and <c>RestoreV6Defaults()</c> puts librdkafka's
/// consistent_random partitioner back so keys stay on their 6.x partitions;
/// GH-6001, every client gets the host's logger factory, so Dekaf's own diagnostics reach the application's logging.
/// </summary>
public class kafka_client_builders_5999_6000_6001
{
    private static KafkaTransport transportOf(WolverineOptions opts) => opts.Transports.GetOrCreate<KafkaTransport>();

    private static KafkaTransport transportOf(IHost host) =>
        host.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();

    [Fact]
    public void configure_builder_callbacks_compose_in_the_order_they_were_registered()
    {
        var opts = new WolverineOptions();
        var calls = new List<string>();

        opts.UseKafka(KafkaContainerFixture.ConnectionString)
            .ConfigureProducerBuilders(_ => calls.Add("producer 1"))
            .ConfigureProducerBuilders(_ => calls.Add("producer 2"))
            .ConfigureConsumerBuilders(_ => calls.Add("consumer 1"))
            .ConfigureConsumerBuilders(_ => calls.Add("consumer 2"))
            .ConfigureAdminClientBuilders(_ => calls.Add("admin 1"))
            .ConfigureAdminClientBuilders(_ => calls.Add("admin 2"));

        var transport = transportOf(opts);
        transport.ConfigureProducerBuilders(DekafClient.CreateProducer<string, byte[]>());
        transport.ConfigureConsumerBuilders(DekafClient.CreateConsumer<string, byte[]>());
        transport.ConfigureAdminClientBuilders(DekafClient.CreateAdminClient());

        calls.ShouldBe(["producer 1", "producer 2", "consumer 1", "consumer 2", "admin 1", "admin 2"]);
    }

    [Fact]
    public async Task dekaf_clients_log_through_the_host_logger_factory()
    {
        var provider = new RecordingLoggerProvider();
        var topicName = "builder-logging-" + Guid.NewGuid().ToString("N")[..8];

        using (var host = await WolverineHost.ForAsync(opts =>
               {
                   opts.Services.AddLogging(x => x.SetMinimumLevel(LogLevel.Trace));
                   opts.Services.AddSingleton<ILoggerProvider>(provider);

                   opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                   opts.PublishAllMessages().ToKafkaTopic(topicName).SendInline();
               }))
        {
            transportOf(host).LoggerFactory.ShouldNotBeNull();

            await host.SendAsync(new ColorChosen { Name = "Blue" });
        }

        // Dekaf creates its loggers from the factory it was given, so its categories are its own types
        provider.Categories.Keys.ShouldContain(x => x.StartsWith("Dekaf", StringComparison.Ordinal));
    }

    [Fact]
    public void the_partitioner_is_left_to_dekaf_by_default()
    {
        var opts = new WolverineOptions();
        opts.UseKafka(KafkaContainerFixture.ConnectionString);

        transportOf(opts).ProducerConfig.Partitioner.ShouldBeNull();
    }

    [Fact]
    public async Task RestoreV6Defaults_uses_librdkafkas_partitioner_on_every_producer_without_one()
    {
        var plainTopic = "v6-partitioner-" + Guid.NewGuid().ToString("N")[..8];
        var ownOptionsTopic = "v6-partitioner-own-" + Guid.NewGuid().ToString("N")[..8];

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.PublishMessage<ColorChosen>().ToKafkaTopic(plainTopic);
            opts.PublishMessage<RedMessage>().ToKafkaTopic(ownOptionsTopic).ConfigureProducer(p => p.Linger = TimeSpan.Zero);

            // After UseKafka() on purpose: the transport reads the flag when it starts
            opts.RestoreV6Defaults();
        });

        var transport = transportOf(host);
        transport.ProducerConfig.Partitioner.ShouldBe(PartitionerType.ConsistentRandom);
        transport.Topics[ownOptionsTopic].ProducerConfig!.Partitioner.ShouldBe(PartitionerType.ConsistentRandom);
    }

    [Fact]
    public async Task an_explicit_partitioner_wins_over_RestoreV6Defaults()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.RestoreV6Defaults();
            opts.UseKafka(KafkaContainerFixture.ConnectionString)
                .ConfigureProducers(p => p.Partitioner = PartitionerType.Murmur2);
        });

        transportOf(host).ProducerConfig.Partitioner.ShouldBe(PartitionerType.Murmur2);
    }

    [Fact]
    public async Task a_restored_producer_puts_every_key_on_the_partition_librdkafka_chose()
    {
        var topicName = "v6-partitions-" + Guid.NewGuid().ToString("N")[..8];

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.RestoreV6Defaults();
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.PublishAllMessages().ToKafkaTopic(topicName).Specification(spec => spec.NumPartitions = 12);
        });

        var keys = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? $"{i:0000}" : Guid.NewGuid().ToString()).ToArray();

        var transport = transportOf(host);
        await using var wolverineProducer = await transport.CreateProducerAsync(transport.ProducerConfig);
        var restored = await partitionsOf(wolverineProducer, topicName, keys);

        // Dekaf's ConsistentRandom matches librdkafka's consistent_random key for key (GH-6000)
        await using var librdkafkaLike = await DekafClient.CreateProducer<string, byte[]>()
            .WithBootstrapServers(KafkaContainerFixture.ConnectionString)
            .WithPartitioner(PartitionerType.ConsistentRandom)
            .BuildAsync(CancellationToken.None);
        var expected = await partitionsOf(librdkafkaLike, topicName, keys);

        await using var dekafDefault = await DekafClient.CreateProducer<string, byte[]>()
            .WithBootstrapServers(KafkaContainerFixture.ConnectionString)
            .BuildAsync(CancellationToken.None);
        var unrestored = await partitionsOf(dekafDefault, topicName, keys);

        restored.ShouldBe(expected);

        // Proves the comparison could fail: Dekaf's own default moves most of these keys
        unrestored.ShouldNotBe(expected);
    }

    private static async Task<int[]> partitionsOf(IKafkaProducer<string, byte[]> producer, string topic, string[] keys)
    {
        var value = "v"u8.ToArray();
        var partitions = new int[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var metadata = await producer.ProduceAsync(
                new ProducerMessage<string, byte[]> { Topic = topic, Key = keys[i], Value = value },
                CancellationToken.None);
            partitions[i] = metadata.Partition;
        }

        return partitions;
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentDictionary<string, int> Categories { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(string category, RecordingLoggerProvider parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                parent.Categories.AddOrUpdate(category, 1, (_, count) => count + 1);
            }
        }
    }
}
