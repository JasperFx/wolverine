using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class publish_raw_json_wire_format
{
    [Fact]
    public async Task raw_json_endpoints_actually_register_the_json_only_mapper()
    {
        // Regression for GH-3407: with the lambda parameters in the (endpoint, mapper) order,
        // UseInterop bound to the Action customization overload, the JsonOnlyMapper was
        // discarded, and both ReceiveRawJson and PublishRawJson silently ran the default
        // KafkaEnvelopeMapper instead.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Policies.DisableConventionalLocalRouting();

                opts.ListenToKafkaTopic("mapper-registration-in").ReceiveRawJson<ColorMessage>();
                opts.PublishAllMessages().ToKafkaTopic("mapper-registration-out").PublishRawJson();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<KafkaTransport>();

        transport.Topics["mapper-registration-in"].BuildMapper(runtime).ShouldBeOfType<JsonOnlyMapper>();
        transport.Topics["mapper-registration-out"].BuildMapper(runtime).ShouldBeOfType<JsonOnlyMapper>();

        await host.StopAsync();
    }

    [Fact]
    public async Task published_records_carry_no_wolverine_headers_on_the_wire()
    {
        // PublishRawJson promises "pure JSON and no other Wolverine metadata". Read the
        // record back with a plain Confluent consumer to prove nothing leaks to an
        // external, non-Wolverine subscriber.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishAllMessages().ToKafkaTopic("clean-json").PublishRawJson();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // The topic may hold records from prior runs, so tag this run's message with a
        // unique color and scan for exactly that record.
        var color = Guid.NewGuid().ToString();

        var config = new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = Guid.NewGuid().ToString(),
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe("clean-json");

        await host.MessageBus().PublishAsync(new ColorMessage(color));

        ConsumeResult<string, byte[]>? match = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (match == null && DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message.Value == null)
            {
                continue;
            }

            if (Encoding.UTF8.GetString(result.Message.Value).Contains(color))
            {
                match = result;
            }
        }

        match.ShouldNotBeNull();
        (match.Message.Headers?.Any() ?? false).ShouldBeFalse();

        // The body is written by Wolverine's default serializer, which camel-cases
        // property names — the raw-JSON mapper changes what headers ride on the record,
        // never the body bytes themselves.
        JsonSerializer.Deserialize<ColorMessage>(
                match.Message.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .Color.ShouldBe(color);

        consumer.Close();
        await host.StopAsync();
    }
}
