using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class raw_json_serializer_options
{
    [Fact]
    public async Task receive_raw_json_honors_the_supplied_options_for_deserialization()
    {
        // Wolverine's default serializer is camel-cased + case-insensitive, which cannot
        // match snake_cased JSON — so this only passes if the options supplied to
        // ReceiveRawJson actually drive deserialization.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        // Unique per run so records left behind by prior runs can't satisfy (or confuse)
        // the tracked wait condition below
        var topic = "snake-json-" + Guid.NewGuid().ToString("N")[..8];

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision()
                    // The consumer group has no committed offsets on this freshly provisioned
                    // topic, and the librdkafka default of Latest would race the producer below
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);
                opts.Discovery.IncludeAssembly(GetType().Assembly);

                opts.ListenToKafkaTopic(topic).ReceiveRawJson<WireMessage>(options);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var transport = receiver.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();
        var colorName = Guid.NewGuid().ToString();

        var session = await receiver.TrackActivity()
            .WaitForMessageToBeReceivedAt<WireMessage>(receiver)
            // The default 5s tracked-session timeout is not enough for a consumer group
            // join on a freshly auto-provisioned topic
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                using var producer = new ProducerBuilder<string, byte[]>(transport.ProducerConfig).Build();
                await producer.ProduceAsync(topic, new Message<string, byte[]>
                {
                    Value = Encoding.UTF8.GetBytes($"{{\"color_name\":\"{colorName}\"}}")
                });
                producer.Flush();
            }));

        session.Received.SingleMessage<WireMessage>()
            .ColorName.ShouldBe(colorName);

        await receiver.StopAsync();
    }

    [Fact]
    public async Task publish_raw_json_honors_the_supplied_options_for_serialization()
    {
        // Plain JsonSerializerOptions serialize Pascal-cased, unlike Wolverine's camel-cased
        // default — so a Pascal-cased body on the wire proves the supplied options were used.
        var topic = "pascal-json-" + Guid.NewGuid().ToString("N")[..8];

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishAllMessages().ToKafkaTopic(topic)
                    .PublishRawJson(new JsonSerializerOptions());

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var colorName = Guid.NewGuid().ToString();

        var config = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = Guid.NewGuid().ToString(),
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(topic);

        await sender.MessageBus().PublishAsync(new WireMessage { ColorName = colorName });

        string? body = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (body == null && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message.Value == null)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(result.Message.Value);
            if (json.Contains(colorName))
            {
                body = json;
            }
        }

        body.ShouldNotBeNull();
        body.ShouldContain($"\"ColorName\":\"{colorName}\"");

        consumer.Close();
        await sender.StopAsync();
    }
}

public class WireMessage
{
    public string ColorName { get; set; } = null!;
}

public static class WireMessageHandler
{
    public static void Handle(WireMessage message)
    {
        // nothing to do — reception is asserted through the tracked session
    }
}
