using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avro;
using Avro.Specific;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Pulsar.Schemas;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3213: Pulsar Avro schema support. The message body is genuine Avro on the wire and the broker
// registers the Avro schema for the topic, while the existing byte-oriented listener/sender plumbing is
// reused (the codec encodes/decodes the message object).
public class pulsar_avro_schema
{
    [Fact]
    public void avro_codec_reports_an_avro_schema_info()
    {
        var codec = new PulsarAvroCodec<AvroOrder>();
        codec.SchemaInfo.Type.ShouldBe(DotPulsar.SchemaType.Avro);
        codec.MessageType.ShouldBe(typeof(AvroOrder));
    }

    [Fact]
    public void avro_codec_round_trips_the_message_bytes()
    {
        var codec = new PulsarAvroCodec<AvroOrder>();
        var order = new AvroOrder { Id = "abc", Amount = 19.5, Quantity = 7 };

        var bytes = codec.Encode(order);
        var back = codec.Decode(bytes).ShouldBeOfType<AvroOrder>();

        back.Id.ShouldBe("abc");
        back.Amount.ShouldBe(19.5);
        back.Quantity.ShouldBe(7);
    }

    [Fact]
    public async Task round_trips_under_an_avro_schema_and_registers_it_with_the_broker()
    {
        var shortName = $"schema-avro-{Guid.NewGuid():N}";
        var topic = $"persistent://public/default/{shortName}";

        using var listener = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .UseAvroSchema<AvroOrder>()
                .ProcessInline()
                .BeginAtEarliest();
            opts.Services.AddSingleton<AvroSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<AvroOrderHandler>();
        });

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).UseAvroSchema<AvroOrder>().SendInline();
        });

        var order = new AvroOrder { Id = "order-1", Amount = 42.5, Quantity = 3 };
        await publisher.SendAsync(order);

        var sink = listener.Services.GetRequiredService<AvroSink>();
        var received = await waitForOneAsync(sink.Received);
        received.Id.ShouldBe(order.Id);
        received.Amount.ShouldBe(order.Amount);
        received.Quantity.ShouldBe(order.Quantity);

        // The broker must have registered an Avro schema for the topic.
        var registered = await getRegisteredSchemaAsync(shortName);
        registered.ShouldNotBeNull("No schema was registered with the broker for the topic");
        registered.Value.GetProperty("type").GetString().ShouldBe("AVRO");
    }

    private static async Task<AvroOrder> waitForOneAsync(ConcurrentBag<AvroOrder> bag, int timeoutMs = 30000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (bag.TryPeek(out var order)) return order;
            await Task.Delay(100);
        }

        throw new TimeoutException("No message received within the timeout");
    }

    private static async Task<JsonElement?> getRegisteredSchemaAsync(string topicShortName)
    {
        using var http = new HttpClient();
        var url = $"{PulsarContainerFixture.HttpServiceUrl}/admin/v2/schemas/public/default/{topicShortName}/schema";

        var cutoff = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            var response = await http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return JsonDocument.Parse(body).RootElement.Clone();
            }

            await Task.Delay(250);
        }

        return null;
    }
}

// A minimal hand-rolled Avro ISpecificRecord (the shape `avrogen` would generate). The `Schema` property
// is [JsonIgnore]'d so Wolverine's outgoing body serialization (unused — the Avro codec owns the wire
// bytes) doesn't try to serialize the Avro schema object.
public class AvroOrder : ISpecificRecord
{
    public static readonly Schema _SCHEMA = Schema.Parse(
        """
        {
          "type": "record",
          "name": "AvroOrder",
          "namespace": "Wolverine.Pulsar.Tests",
          "fields": [
            { "name": "Id", "type": "string" },
            { "name": "Amount", "type": "double" },
            { "name": "Quantity", "type": "int" }
          ]
        }
        """);

    public string Id { get; set; } = string.Empty;
    public double Amount { get; set; }
    public int Quantity { get; set; }

    [JsonIgnore]
    public Schema Schema => _SCHEMA;

    public object Get(int fieldPos) => fieldPos switch
    {
        0 => Id,
        1 => Amount,
        2 => Quantity,
        _ => throw new AvroRuntimeException($"Bad index {fieldPos} in AvroOrder.Get()")
    };

    public void Put(int fieldPos, object fieldValue)
    {
        switch (fieldPos)
        {
            case 0:
                Id = (string)fieldValue;
                break;
            case 1:
                Amount = (double)fieldValue;
                break;
            case 2:
                Quantity = (int)fieldValue;
                break;
            default:
                throw new AvroRuntimeException($"Bad index {fieldPos} in AvroOrder.Put()");
        }
    }
}

public class AvroSink
{
    public ConcurrentBag<AvroOrder> Received { get; } = new();
}

public class AvroOrderHandler
{
    public static void Handle(AvroOrder order, AvroSink sink)
    {
        sink.Received.Add(order);
    }
}
