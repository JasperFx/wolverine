using Confluent.SchemaRegistry;
using NSubstitute;
using Shouldly;
using Wolverine.Kafka.Serialization;

namespace Wolverine.Kafka.Tests.Serialization;

public class schema_registry_serializer_tests
{
    private readonly ISchemaRegistryClient _mockRegistry = Substitute.For<ISchemaRegistryClient>();

    [Fact]
    public void json_serializer_has_correct_content_type()
    {
        var serializer = new SchemaRegistryJsonSerializer(_mockRegistry);
        serializer.ContentType.ShouldBe("application/json");
    }

    [Fact]
    public void avro_serializer_has_correct_content_type()
    {
        var serializer = new SchemaRegistryAvroSerializer(_mockRegistry);
        serializer.ContentType.ShouldBe("application/avro");
    }

    [Fact]
    public void json_serializer_throws_on_null_registry()
    {
        Should.Throw<ArgumentNullException>(() =>
            new SchemaRegistryJsonSerializer(null!));
    }

    [Fact]
    public void avro_serializer_throws_on_null_registry()
    {
        Should.Throw<ArgumentNullException>(() =>
            new SchemaRegistryAvroSerializer(null!));
    }

    [Fact]
    public void json_serializer_write_throws_on_null_message()
    {
        var serializer = new SchemaRegistryJsonSerializer(_mockRegistry);
        var envelope = new Envelope { Message = null };

        Should.Throw<ArgumentNullException>(() => serializer.Write(envelope));
    }

    [Fact]
    public void avro_serializer_write_throws_on_null_message()
    {
        var serializer = new SchemaRegistryAvroSerializer(_mockRegistry);
        var envelope = new Envelope { Message = null };

        Should.Throw<ArgumentNullException>(() => serializer.Write(envelope));
    }

    [Fact]
    public void json_read_from_raw_bytes_is_not_supported()
    {
        var serializer = new SchemaRegistryJsonSerializer(_mockRegistry);

        Should.Throw<NotSupportedException>(() =>
            serializer.ReadFromData(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void avro_read_from_raw_bytes_is_not_supported()
    {
        var serializer = new SchemaRegistryAvroSerializer(_mockRegistry);

        Should.Throw<NotSupportedException>(() =>
            serializer.ReadFromData(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void json_read_from_data_throws_on_empty_envelope()
    {
        var serializer = new SchemaRegistryJsonSerializer(_mockRegistry);
        var envelope = new Envelope();

        // Envelope with no message or data throws during deserialization
        Should.Throw<Exception>(() =>
            serializer.ReadFromData(typeof(SampleKafkaMessage), envelope));
    }

    [Fact]
    public void avro_read_from_data_throws_on_empty_envelope()
    {
        var serializer = new SchemaRegistryAvroSerializer(_mockRegistry);
        var envelope = new Envelope();

        // Envelope with no message or data throws during deserialization
        Should.Throw<Exception>(() =>
            serializer.ReadFromData(typeof(SampleKafkaMessage), envelope));
    }

    [Fact]
    public void json_serializer_implements_IMessageSerializer()
    {
        var serializer = new SchemaRegistryJsonSerializer(_mockRegistry);
        serializer.ShouldBeAssignableTo<Wolverine.Runtime.Serialization.IMessageSerializer>();
    }

    [Fact]
    public void avro_serializer_implements_IMessageSerializer()
    {
        var serializer = new SchemaRegistryAvroSerializer(_mockRegistry);
        serializer.ShouldBeAssignableTo<Wolverine.Runtime.Serialization.IMessageSerializer>();
    }
}

public record SampleKafkaMessage(string Name, int Value);
