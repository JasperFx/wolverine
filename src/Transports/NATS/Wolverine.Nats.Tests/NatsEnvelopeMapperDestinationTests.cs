using System.Reflection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NSubstitute;
using Shouldly;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// The mappers used to derive <see cref="Envelope.Destination"/> from every incoming subject and kept each Uri in a
/// static cache that nothing ever emptied: one entry per distinct subject under a wildcard listener, and -- with the
/// per-request reply subject -- one per <c>InvokeAsync()</c> reply answered over core NATS, surviving every host in
/// the process. The receiving pipeline replaces <c>Destination</c> with the listener's address anyway, so the
/// mappers take the endpoint's own Uri and keep no state.
/// </summary>
public class NatsEnvelopeMapperDestinationTests
{
    [Fact]
    public void a_core_message_gets_the_listener_endpoint_as_destination()
    {
        var endpoint = new NatsTransport().EndpointForSubject("orders.>");
        var mapper = new NatsEnvelopeMapper(endpoint);

        foreach (var subject in new[] { "orders.1", "orders.2", "wolverine.response.service.1.0f0f" })
        {
            var message = new NatsMsg<byte[]>(subject, null, 0, new NatsHeaders { ["message-type"] = "ping" },
                [1], null);
            var envelope = new NatsEnvelope(message, null);

            mapper.MapIncomingToEnvelope(envelope, message);

            envelope.Destination.ShouldBe(endpoint.Uri);
        }
    }

    [Fact]
    public void a_jetstream_message_gets_the_listener_endpoint_as_destination()
    {
        var endpoint = new NatsTransport().EndpointForSubject("orders.>");
        var mapper = new JetStreamEnvelopeMapper(endpoint);

        foreach (var subject in new[] { "orders.1", "orders.2" })
        {
            var message = Substitute.For<INatsJSMsg<byte[]>>();
            message.Subject.Returns(subject);
            message.Data.Returns([1]);
            message.Headers.Returns(new NatsHeaders { ["message-type"] = "ping" });
            var envelope = new NatsEnvelope(null, message);

            mapper.MapIncomingToEnvelope(envelope, message);

            envelope.Destination.ShouldBe(endpoint.Uri);
        }
    }

    [Fact]
    public void a_named_broker_keeps_its_scheme()
    {
        var endpoint = new NatsTransport("secondary").EndpointForSubject("orders.>");
        var mapper = new NatsEnvelopeMapper(endpoint);
        var message = new NatsMsg<byte[]>("orders.1", null, 0, new NatsHeaders { ["message-type"] = "ping" }, [1],
            null);
        var envelope = new NatsEnvelope(message, null);

        mapper.MapIncomingToEnvelope(envelope, message);

        envelope.Destination!.Scheme.ShouldBe("secondary");
    }

    [Fact]
    public void the_mappers_keep_no_static_state()
    {
        foreach (var type in new[] { typeof(NatsEnvelopeMapper), typeof(JetStreamEnvelopeMapper) })
        {
            type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(x => !x.IsLiteral && !x.IsInitOnly)
                .Select(x => $"{type.Name}.{x.Name}")
                .ShouldBeEmpty();
        }
    }
}
