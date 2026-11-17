using Dekaf.Consumer;
using Dekaf.Producer;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-5201, against the docker-compose Kafka broker. Kafka producers are idempotent by default in Wolverine 7.0
/// (Dekaf's default, which Wolverine leaves alone). <c>DisableIdempotentProducer()</c> opts out,
/// <c>RestoreV6Defaults()</c> restores the 6.x Confluent.Kafka behaviour (idempotence off wherever it was not set),
/// and an acks setting an idempotent producer cannot honour stops the host with a message naming the opt-out.
/// </summary>
public class kafka_idempotent_producer_default_5201
{
    private const int MillisecondsTimeout = 30_000;

    private static async Task<IHost> startPublisherAsync(string topicName, Action<WolverineOptions, KafkaTransportExpression> configure)
    {
        return await WolverineHost.ForAsync(opts =>
        {
            var kafka = opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
            configure(opts, kafka);

            opts.PublishAllMessages().ToKafkaTopic(topicName).SendInline();
        });
    }

    private static async Task sendAndReceiveAsync(IHost publisher, string topicName)
    {
        var callback = new TaskCompletionSource<ColorChosen>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.ListenToKafkaTopic(topicName)
                .ProcessInline()
                .ConfigureConsumer(x =>
                {
                    x.GroupId = Guid.NewGuid().ToString();
                    x.AutoOffsetReset = AutoOffsetReset.Earliest;
                });
            opts.Services.AddSingleton(callback);

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<ColorHandler>();
        });

        var message = new ColorChosen { Name = Guid.NewGuid().ToString() };
        await publisher.SendAsync(message);

        var received = await callback.Task.TimeoutAfterAsync(MillisecondsTimeout);
        received.Name.ShouldBe(message.Name);
    }

    private static KafkaTransport transportOf(IHost host) =>
        host.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();

    [Fact]
    public async Task producers_are_idempotent_by_default()
    {
        var topicName = "idempotent-default-" + Guid.NewGuid().ToString("N")[..8];
        using var publisher = await startPublisherAsync(topicName, (_, _) => { });

        transportOf(publisher).ProducerConfig.EnableIdempotence.ShouldBe(true);

        await sendAndReceiveAsync(publisher, topicName);
    }

    [Theory]
    [InlineData(Acks.Leader)]
    [InlineData(Acks.None)]
    public async Task acks_an_idempotent_producer_cannot_use_stop_the_host_and_name_the_opt_out(Acks acks)
    {
        var topicName = "idempotent-acks-" + Guid.NewGuid().ToString("N")[..8];

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await startPublisherAsync(topicName,
                (_, kafka) => kafka.ConfigureProducers(p => p.Acks = acks));
        });

        // Startup may wrap it, so find the transport's own exception
        var conflict = ex;
        while (conflict.Message.IndexOf("idempotent", StringComparison.Ordinal) < 0 && conflict.InnerException != null)
        {
            conflict = conflict.InnerException;
        }

        conflict.Message.ShouldContain("DisableIdempotentProducer()");
        conflict.Message.ShouldContain($"Acks = {acks}");
    }

    [Fact]
    public async Task disable_idempotent_producer_opts_out_and_allows_lower_acks()
    {
        var topicName = "idempotent-off-" + Guid.NewGuid().ToString("N")[..8];
        using var publisher = await startPublisherAsync(topicName,
            (_, kafka) => kafka.DisableIdempotentProducer().ConfigureProducers(p => p.Acks = Acks.Leader));

        transportOf(publisher).ProducerConfig.EnableIdempotence.ShouldBe(false);

        await sendAndReceiveAsync(publisher, topicName);
    }

    [Fact]
    public async Task disable_idempotent_producer_on_one_topic()
    {
        var topicName = "idempotent-topic-off-" + Guid.NewGuid().ToString("N")[..8];
        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

            opts.PublishAllMessages().ToKafkaTopic(topicName)
                .ConfigureProducer(p => p.Acks = Acks.Leader)
                .DisableIdempotentProducer()
                .SendInline();
        });

        var transport = transportOf(publisher);
        transport.ProducerConfig.EnableIdempotence.ShouldBe(true);
        transport.Topics[topicName].ProducerConfig!.EnableIdempotence.ShouldBe(false);

        await sendAndReceiveAsync(publisher, topicName);
    }

    [Fact]
    public async Task RestoreV6Defaults_turns_idempotence_off_and_keeps_6x_acks_working()
    {
        var topicName = "idempotent-v6-" + Guid.NewGuid().ToString("N")[..8];

        // RestoreV6Defaults() after UseKafka() on purpose: the transport reads the flag when it starts
        using var publisher = await startPublisherAsync(topicName, (opts, kafka) =>
        {
            kafka.ConfigureProducers(p => p.Acks = Acks.Leader);
            opts.RestoreV6Defaults();
        });

        transportOf(publisher).ProducerConfig.EnableIdempotence.ShouldBe(false);

        await sendAndReceiveAsync(publisher, topicName);
    }

    [Fact]
    public async Task RestoreV6Defaults_also_covers_a_topic_with_its_own_producer_options()
    {
        var topicName = "idempotent-v6-topic-" + Guid.NewGuid().ToString("N")[..8];
        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.RestoreV6Defaults();
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

            opts.PublishAllMessages().ToKafkaTopic(topicName)
                .ConfigureProducer(p => p.Acks = Acks.Leader)
                .SendInline();
        });

        transportOf(publisher).Topics[topicName].ProducerConfig!.EnableIdempotence.ShouldBe(false);

        await sendAndReceiveAsync(publisher, topicName);
    }

    [Fact]
    public async Task an_explicit_UseIdempotentProducer_wins_over_RestoreV6Defaults()
    {
        var topicName = "idempotent-v6-explicit-" + Guid.NewGuid().ToString("N")[..8];
        using var publisher = await startPublisherAsync(topicName, (opts, kafka) =>
        {
            opts.RestoreV6Defaults();
            kafka.UseIdempotentProducer();
        });

        transportOf(publisher).ProducerConfig.EnableIdempotence.ShouldBe(true);

        await sendAndReceiveAsync(publisher, topicName);
    }
}
