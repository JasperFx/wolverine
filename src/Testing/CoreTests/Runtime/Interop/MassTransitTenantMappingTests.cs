using NSubstitute;
using Wolverine.Runtime.Interop.MassTransit;
using Xunit;

namespace CoreTests.Runtime.Interop;

public class MassTransitTenantMappingTests
{
    private readonly IMassTransitInteropEndpoint theEndpoint = Substitute.For<IMassTransitInteropEndpoint>();

    public MassTransitTenantMappingTests()
    {
        // The serializer round-trips a response address through the envelope; give the fake
        // endpoint a valid reply Uri so the write path doesn't emit an empty address.
        theEndpoint.MassTransitReplyUri().Returns(new Uri("rabbitmq://localhost/responses"));
    }

    // Round-trips a message through the MassTransit serializer: writes it in the
    // MassTransit envelope format, then reads it back the way an incoming message would be
    // deserialized. Returns the incoming Envelope so tests can assert on mapped metadata.
    private Envelope readIncoming(MassTransitJsonSerializer serializer, object message)
    {
        var outgoing = new Envelope { Id = Guid.NewGuid(), Message = message };
        var data = serializer.Write(outgoing);

        var incoming = new Envelope { Data = data };
        serializer.ReadFromData(message.GetType(), incoming);
        return incoming;
    }

    [Fact]
    public void maps_tenant_id_from_the_incoming_message_body()
    {
        var serializer = new MassTransitJsonSerializer(theEndpoint);
        serializer.MapTenantIdFrom<TenantMessage>(env => env.Message?.TenantId);

        var incoming = readIncoming(serializer, new TenantMessage("acme", "hello"));

        incoming.TenantId.ShouldBe("acme");
    }

    [Fact]
    public void maps_tenant_id_from_a_masstransit_header()
    {
        var serializer = new MassTransitJsonSerializer(theEndpoint);
        serializer.MapTenantIdFrom<TenantMessage>(env =>
            env.Headers.TryGetValue("tenant-id", out var value) ? value?.ToString() : null);

        var outgoing = new Envelope { Id = Guid.NewGuid(), Message = new TenantMessage("ignored", "hello") };
        outgoing.Headers["tenant-id"] = "globex";
        var data = serializer.Write(outgoing);

        var incoming = new Envelope { Data = data };
        serializer.ReadFromData(typeof(TenantMessage), incoming);

        incoming.TenantId.ShouldBe("globex");
    }

    [Fact]
    public void leaves_tenant_id_untouched_for_an_unregistered_message_type()
    {
        var serializer = new MassTransitJsonSerializer(theEndpoint);
        serializer.MapTenantIdFrom<TenantMessage>(env => env.Message?.TenantId);

        var incoming = readIncoming(serializer, new OtherMessage("no tenant here"));

        incoming.TenantId.ShouldBeNull();
    }

    [Fact]
    public void does_not_overwrite_tenant_id_when_the_source_returns_empty()
    {
        var serializer = new MassTransitJsonSerializer(theEndpoint);
        serializer.MapTenantIdFrom<TenantMessage>(_ => "");

        var incoming = readIncoming(serializer, new TenantMessage("acme", "hello"));

        incoming.TenantId.ShouldBeNull();
    }

    [Fact]
    public void supports_multiple_registered_message_types()
    {
        var serializer = new MassTransitJsonSerializer(theEndpoint);
        serializer
            .MapTenantIdFrom<TenantMessage>(env => env.Message?.TenantId)
            .MapTenantIdFrom<AnotherTenantMessage>(env => env.Message?.Org);

        readIncoming(serializer, new TenantMessage("acme", "hello")).TenantId.ShouldBe("acme");
        readIncoming(serializer, new AnotherTenantMessage("globex", "hi")).TenantId.ShouldBe("globex");
    }

    [Fact]
    public void falls_through_the_whole_mapper_chain_for_an_unregistered_type()
    {
        var serializer = new MassTransitJsonSerializer(theEndpoint);
        serializer
            .MapTenantIdFrom<TenantMessage>(env => env.Message?.TenantId)
            .MapTenantIdFrom<AnotherTenantMessage>(env => env.Message?.Org);

        var incoming = readIncoming(serializer, new OtherMessage("matches nothing"));

        incoming.TenantId.ShouldBeNull();
    }
}

public record TenantMessage(string TenantId, string Body);
public record AnotherTenantMessage(string Org, string Body);
public record OtherMessage(string Body);
