using System.Text;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class masstransit_interop_serializer_on_retry
{
    // Reproduces the MassTransit interop "double path" bug: UseMassTransitInterop
    // registers its serializer only on the listener endpoint. At first receipt the
    // envelope carries that serializer, but on a replay (scheduled retry, durable
    // recovery) the runtime-only Serializer reference is gone. The deserialization
    // path then has to re-resolve the serializer, and resolving from the global
    // content-type registry (which never saw "application/vnd.masstransit+json")
    // falls back to the default JSON serializer. That deserializes the *un-unwrapped*
    // MassTransit envelope root, so every field on the real message comes back as a
    // default value (here, OrderId 0).
    [Fact]
    public async Task replayed_masstransit_envelope_is_unwrapped_on_the_retry_path()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();
                opts.IncludeType<OrderPlacedHandler>();

                // Bogus host name on purpose: stubbed transports must never connect.
                opts.UseRabbitMq(x => x.HostName = Guid.NewGuid().ToString());
                opts.ListenToRabbitQueue("orders").UseMassTransitInterop();

                opts.StubAllExternalTransports();
            }).StartAsync();

        var runtime = host.GetRuntime();

        var endpoint = runtime.Endpoints.EndpointFor(new Uri("rabbitmq://queue/orders"))
            .ShouldNotBeNull()
            .ShouldBeAssignableTo<RabbitMqEndpoint>();

        // Building the envelope mapper is what applies UseMassTransitInterop and wires
        // the MassTransit serializer onto the endpoint. A live listener does this at
        // startup; StubAllExternalTransports() skips listener startup, so trigger it
        // here to reach the same post-startup endpoint state.
        endpoint.BuildMapper(runtime);

        // The wire payload a MassTransit producer actually sends: the real message
        // lives under "message", not at the root.
        var json = $$"""
        {
            "messageId": "{{Guid.NewGuid()}}",
            "messageType": ["urn:message:Orders:OrderPlaced"],
            "message": { "orderId": 92883 }
        }
        """;

        // Rebuild the envelope the way durable storage hands it back on a retry:
        // ContentType + Destination + Data are persisted, but Serializer is not.
        var replayed = new Envelope
        {
            Data = Encoding.UTF8.GetBytes(json),
            ContentType = "application/vnd.masstransit+json",
            MessageType = typeof(OrderPlaced).ToMessageTypeName(),
            Destination = endpoint.Uri
        };

        // The replay precondition: no Serializer is attached, so resolution must go
        // through serializerFor(envelope) — the code path under test.
        replayed.Serializer.ShouldBeNull();

        var continuation = await runtime.Pipeline.TryDeserializeEnvelope(replayed);

        // A clean deserialization yields NullContinuation. Asserting it here surfaces a
        // deserialization failure (MoveToErrorQueue) directly instead of as a confusing
        // null Message on the next assertion.
        continuation.ShouldBeOfType<NullContinuation>();

        replayed.Message.ShouldBeOfType<OrderPlaced>()
            .OrderId.ShouldBe(92883);
    }

    public record OrderPlaced(int OrderId);

    public class OrderPlacedHandler
    {
        // Presence registers OrderPlaced in the HandlerGraph so the pipeline can
        // resolve its message type during deserialization.
        public void Handle(OrderPlaced order)
        {
        }
    }
}
