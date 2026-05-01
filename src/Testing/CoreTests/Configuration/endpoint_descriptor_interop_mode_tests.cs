using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// Coverage for the <c>"Custom"</c> interop-mode signal added in #2641. The
/// descriptor's <see cref="EndpointDescriptor.InteropMode"/> reports
/// <c>"Custom"</c> when an endpoint has a non-default
/// <see cref="IEnvelopeMapper"/> wired in, regardless of which serializer
/// happens to be attached (mapper signal wins). Also locks down the new
/// <see cref="EndpointDescriptor.DefaultSerializerDescription"/> friendly-name
/// rendering.
/// </summary>
public class endpoint_descriptor_interop_mode_tests
{
    // ---- Base contract on the abstract Endpoint ----

    [Fact]
    public void default_typed_endpoint_with_no_mapper_override_reports_no_custom_mapper()
    {
        var endpoint = new TestTypedEndpoint();

        endpoint.HasCustomEnvelopeMapper.ShouldBeFalse();
    }

    [Fact]
    public void typed_endpoint_with_explicit_mapper_reports_custom_mapper()
    {
        var endpoint = new TestTypedEndpoint
        {
            EnvelopeMapper = new TestMapper()
        };

        endpoint.HasCustomEnvelopeMapper.ShouldBeTrue();
    }

    [Fact]
    public void typed_endpoint_with_registered_mapper_factory_reports_custom_mapper()
    {
        var endpoint = new TestTypedEndpoint();
        endpoint.RegisterMapperFactoryForTest(_ => new TestMapper());

        endpoint.HasCustomEnvelopeMapper.ShouldBeTrue();
    }

    [Fact]
    public void non_typed_endpoint_always_reports_no_custom_mapper()
    {
        // Endpoints that do not derive from Endpoint<TMapper, TConcreteMapper> have no
        // mapper-customization concept. Local queues, dbcontrol, oraclecontrol, TCP, etc.
        // should report HasCustomEnvelopeMapper = false (and therefore InteropMode = null
        // unless a serializer name match drives it).
        var endpoint = new LocalQueue("local-x");

        endpoint.HasCustomEnvelopeMapper.ShouldBeFalse();
    }

    // ---- Descriptor.InteropMode ----

    [Fact]
    public void descriptor_reports_null_interop_mode_when_no_mapper_override_and_default_serializer()
    {
        var endpoint = new TestTypedEndpoint
        {
            DefaultSerializer = new SystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions())
        };
        var descriptor = new EndpointDescriptor(endpoint);

        descriptor.InteropMode.ShouldBeNull();
    }

    [Fact]
    public void descriptor_reports_custom_when_mapper_is_overridden_with_default_serializer()
    {
        var endpoint = new TestTypedEndpoint
        {
            EnvelopeMapper = new TestMapper(),
            DefaultSerializer = new SystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions())
        };
        var descriptor = new EndpointDescriptor(endpoint);

        descriptor.InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void custom_mapper_wins_over_well_known_serializer_name()
    {
        // Last-usage-wins: a custom mapper trumps a CloudEvents-named serializer.
        // The mapper is the louder operator-relevant signal.
        var endpoint = new TestTypedEndpoint
        {
            EnvelopeMapper = new TestMapper(),
            DefaultSerializer = new FakeCloudEventsSerializer()
        };
        var descriptor = new EndpointDescriptor(endpoint);

        descriptor.InteropMode.ShouldBe("Custom");
    }

    [Fact]
    public void well_known_cloud_events_serializer_drives_interop_mode_when_no_mapper_override()
    {
        var endpoint = new TestTypedEndpoint
        {
            DefaultSerializer = new FakeCloudEventsSerializer()
        };
        var descriptor = new EndpointDescriptor(endpoint);

        descriptor.InteropMode.ShouldBe("CloudEvents");
    }

    [Theory]
    [InlineData("CloudEventsSerializer", "CloudEvents")]
    [InlineData("NServiceBusSerializer", "NServiceBus")]
    [InlineData("MassTransitJsonSerializer", "MassTransit")]
    [InlineData("RawJsonSerializer", "RawJson")]
    [InlineData("SystemTextJsonSerializer", null)]
    [InlineData("MessagePackSerializer", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void resolve_interop_mode_from_serializer_name(string? typeName, string? expected)
    {
        EndpointDescriptor.ResolveInteropMode(typeName).ShouldBe(expected);
    }

    // ---- Descriptor.DefaultSerializerDescription ----

    [Theory]
    [InlineData("SystemTextJsonSerializer", "System.Text.Json")]
    [InlineData("MessagePackSerializer", "MessagePack")]
    [InlineData("MemoryPackSerializer", "MemoryPack")]
    [InlineData("NewtonsoftJsonSerializer", "Newtonsoft.Json")]
    [InlineData("RawJsonSerializer", "Raw JSON")]
    [InlineData("CloudEventsSerializer", "CloudEvents")]
    [InlineData("NServiceBusSerializer", "NServiceBus")]
    [InlineData("MassTransitJsonSerializer", "MassTransit")]
    [InlineData("ProtobufSerializer", "Protobuf")]
    [InlineData("AvroSerializer", "Avro")]
    [InlineData("MyCompanySpecialSerializer", "MyCompanySpecialSerializer")] // unknown → raw type name
    [InlineData(null, null)]
    [InlineData("", null)]
    public void resolve_default_serializer_description_friendly_names(string? typeName, string? expected)
    {
        EndpointDescriptor.ResolveSerializerDescription(typeName).ShouldBe(expected);
    }

    [Fact]
    public void descriptor_lifts_default_serializer_description_from_endpoint()
    {
        var endpoint = new TestTypedEndpoint
        {
            DefaultSerializer = new SystemTextJsonSerializer(new System.Text.Json.JsonSerializerOptions())
        };
        var descriptor = new EndpointDescriptor(endpoint);

        descriptor.SerializerType.ShouldBe("SystemTextJsonSerializer");
        descriptor.DefaultSerializerDescription.ShouldBe("System.Text.Json");
    }

    [Fact]
    public void descriptor_returns_null_serializer_description_when_no_serializer_attached()
    {
        var endpoint = new TestTypedEndpoint();
        var descriptor = new EndpointDescriptor(endpoint);

        descriptor.SerializerType.ShouldBeNull();
        descriptor.DefaultSerializerDescription.ShouldBeNull();
    }

    // ---- Test plumbing ----

    private interface ITestMapper : IEnvelopeMapper;

    private class TestMapper : ITestMapper
    {
        public void ReceivesMessage(Type messageType) { }
        public void MapPropertyToHeader(System.Linq.Expressions.Expression<Func<Envelope, object>> property, string headerKey) { }
    }

    private class TestTypedEndpoint : Endpoint<ITestMapper, TestMapper>
    {
        public TestTypedEndpoint() : base(new Uri("test://endpoint"), EndpointRole.Application) { }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
            => throw new NotSupportedException();

        protected override Wolverine.Transports.Sending.ISender CreateSender(IWolverineRuntime runtime)
            => throw new NotSupportedException();

        protected override TestMapper buildMapper(IWolverineRuntime runtime) => new();

        // Surface the protected helper so the registered-factory test can exercise it
        // without needing to construct a full ListenerConfiguration to do it indirectly.
        public void RegisterMapperFactoryForTest(Func<IWolverineRuntime, ITestMapper> factory)
            => registerMapperFactory(factory);
    }

    private class FakeCloudEventsSerializer : IMessageSerializer
    {
        public string ContentType => "application/cloudevents+json";
        public byte[] Write(Envelope envelope) => Array.Empty<byte>();
        public object ReadFromData(Type messageType, Envelope envelope) => null!;
        public object ReadFromData(byte[]? data) => null!;
        public byte[] WriteMessage(object message) => Array.Empty<byte>();
    }
}
