using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Runtime.Interop.MassTransit;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

public class masstransit_mapper_tenant_mapping
{
    // The SQS MassTransit mapper must apply the UseMassTransitInterop(configure) lambda to its
    // serializer, otherwise MapTenantIdFrom (and any other serializer customization) silently
    // no-ops on the Amazon SQS listener.
    [Fact]
    public void mapper_applies_the_configure_lambda_to_its_serializer()
    {
        var endpoint = new FakeMassTransitEndpoint();

        var mapper = new MassTransitMapper(endpoint,
            mt => mt.MapTenantIdFrom<SqsTenantMessage>(env => env.Message?.TenantId));

        var serializer = mapper.Serializer;

        var outgoing = new Envelope { Id = Guid.NewGuid(), Message = new SqsTenantMessage("acme", "hello") };
        var data = serializer.Write(outgoing);

        var incoming = new Envelope { Data = data };
        serializer.ReadFromData(typeof(SqsTenantMessage), incoming);

        incoming.TenantId.ShouldBe("acme");
    }

    private sealed class FakeMassTransitEndpoint : IMassTransitInteropEndpoint
    {
        public Uri? MassTransitUri() => null;
        public Uri? MassTransitReplyUri() => new Uri("sqs://responses");
        public Uri? TranslateMassTransitToWolverineUri(Uri uri) => null;
    }

    public record SqsTenantMessage(string TenantId, string Body);
}
