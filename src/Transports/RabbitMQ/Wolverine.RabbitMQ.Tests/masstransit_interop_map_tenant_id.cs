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

public class masstransit_interop_map_tenant_id
{
    // UseMassTransitInterop(configure) must thread the configure lambda all the way down to
    // the MassTransit serializer so MapTenantIdFrom takes effect on the Rabbit MQ listener.
    // Uses stubbed transports (no live broker) and drives the deserialization path directly.
    [Fact]
    public async Task map_tenant_id_from_is_applied_on_the_rabbitmq_listener()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();
                opts.IncludeType<TenantOrderPlacedHandler>();

                // Bogus host name on purpose: stubbed transports must never connect.
                opts.UseRabbitMq(x => x.HostName = Guid.NewGuid().ToString());
                opts.ListenToRabbitQueue("orders")
                    .UseMassTransitInterop(mt => mt.MapTenantIdFrom<TenantOrderPlaced>(env => env.Message?.Tenant));

                opts.StubAllExternalTransports();
            }).StartAsync();

        var runtime = host.GetRuntime();

        var endpoint = runtime.Endpoints.EndpointFor(new Uri("rabbitmq://queue/orders"))
            .ShouldNotBeNull()
            .ShouldBeAssignableTo<RabbitMqEndpoint>();

        // Applies UseMassTransitInterop and wires the MassTransit serializer onto the endpoint
        // (a live listener does this at startup; stubbed transports skip listener startup).
        endpoint.BuildMapper(runtime);

        // The wire payload a MassTransit producer sends: the real message lives under "message".
        var json = $$"""
        {
            "messageId": "{{Guid.NewGuid()}}",
            "messageType": ["urn:message:Orders:TenantOrderPlaced"],
            "message": { "orderId": 92883, "tenant": "acme" }
        }
        """;

        var incoming = new Envelope
        {
            Data = Encoding.UTF8.GetBytes(json),
            ContentType = "application/vnd.masstransit+json",
            MessageType = typeof(TenantOrderPlaced).ToMessageTypeName(),
            Destination = endpoint.Uri
        };

        await runtime.Pipeline.TryDeserializeEnvelope(incoming);

        incoming.Message.ShouldBeOfType<TenantOrderPlaced>().OrderId.ShouldBe(92883);
        incoming.TenantId.ShouldBe("acme");
    }

    public record TenantOrderPlaced(int OrderId, string Tenant);

    public class TenantOrderPlacedHandler
    {
        // Presence registers TenantOrderPlaced in the HandlerGraph so the pipeline can
        // resolve its message type during deserialization.
        public void Handle(TenantOrderPlaced order)
        {
        }
    }
}
