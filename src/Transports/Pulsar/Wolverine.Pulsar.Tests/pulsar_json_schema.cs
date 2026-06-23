using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Pulsar.Schemas;
using Xunit;

namespace Wolverine.Pulsar.Tests;

// GH-3183: Pulsar schema support. A JSON schema is registered with the broker for the topic while
// Wolverine keeps owning the message body bytes (pass-through schema).
public class pulsar_json_schema
{
    // ---- generator unit tests (no broker) ----

    [Fact]
    public void generates_an_avro_record_schema_for_a_poco()
    {
        var json = AvroSchemaGenerator.Generate(typeof(SchemaOrder));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("type").GetString().ShouldBe("record");
        root.GetProperty("name").GetString().ShouldBe("SchemaOrder");

        var fields = root.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type"));

        fields.ContainsKey("Id").ShouldBeTrue();
        fields["Id"].GetString().ShouldBe("string");          // Guid -> string
        fields["Amount"].GetString().ShouldBe("double");
        fields["Quantity"].GetString().ShouldBe("int");
        fields["Note"].GetString().ShouldBe("string");        // reference type -> string
        // Nullable<T> value type -> ["null", T]
        fields["Priority"].ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public void for_json_marks_the_schema_info_as_json()
    {
        var schema = PulsarSchema.ForJson(typeof(SchemaOrder));
        schema.SchemaInfo.Type.ShouldBe(DotPulsar.SchemaType.Json);
        schema.SchemaInfo.Name.ShouldBe("SchemaOrder");
    }

    // ---- end-to-end (Pulsar docker) ----

    [Fact]
    public async Task round_trips_under_a_json_schema_and_registers_it_with_the_broker()
    {
        var shortName = $"schema-json-{Guid.NewGuid():N}";
        var topic = $"persistent://public/default/{shortName}";

        using var listener = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.ListenToPulsarTopic(topic)
                .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                .UseJsonSchema<SchemaOrder>()
                .ProcessInline()
                .BeginAtEarliest();
            opts.Services.AddSingleton<SchemaSink>();
            opts.Discovery.DisableConventionalDiscovery().IncludeType<SchemaOrderHandler>();
        });

        using var publisher = await WolverineHost.ForAsync(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).UseJsonSchema<SchemaOrder>().SendInline();
        });

        var order = new SchemaOrder { Id = Guid.NewGuid(), Amount = 42.5, Quantity = 3, Note = "rush" };
        await publisher.SendAsync(order);

        var sink = listener.Services.GetRequiredService<SchemaSink>();
        var received = await waitForOneAsync(sink.Received);
        received.Id.ShouldBe(order.Id);
        received.Amount.ShouldBe(order.Amount);
        received.Quantity.ShouldBe(order.Quantity);
        received.Note.ShouldBe(order.Note);

        // The broker must have registered a JSON schema for the topic.
        var registered = await getRegisteredSchemaAsync(shortName);
        registered.ShouldNotBeNull("No schema was registered with the broker for the topic");
        registered.Value.GetProperty("type").GetString().ShouldBe("JSON");
    }

    private static async Task<SchemaOrder> waitForOneAsync(ConcurrentBag<SchemaOrder> bag, int timeoutMs = 30000)
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

public class SchemaOrder
{
    public Guid Id { get; set; }
    public double Amount { get; set; }
    public int Quantity { get; set; }
    public string? Note { get; set; }
    public int? Priority { get; set; }
}

public class SchemaSink
{
    public ConcurrentBag<SchemaOrder> Received { get; } = new();
}

public class SchemaOrderHandler
{
    public static void Handle(SchemaOrder order, SchemaSink sink)
    {
        sink.Received.Add(order);
    }
}
