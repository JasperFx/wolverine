using Grpc.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.ServerPropagation;

/// <summary>
///     Proves <see cref="WolverineGrpcOptions.PropagateEnvelopeHeaders"/> = false actually turns the
///     server-side interceptor off, even when a caller sends the envelope headers on the wire.
/// </summary>
public class server_propagation_disabled_tests : IClassFixture<DisabledPropagationFixture>
{
    private readonly DisabledPropagationFixture _fixture;

    public server_propagation_disabled_tests(DisabledPropagationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task handler_does_not_see_headers_when_propagation_is_disabled()
    {
        var headers = new Metadata
        {
            { EnvelopeConstants.CorrelationIdKey, "corr-should-be-ignored" },
            { EnvelopeConstants.TenantIdKey, "tenant-should-be-ignored" }
        };

        var client = _fixture.CreateClient();
        var reply = await client.Echo(new PropagationEchoRequest(), new CallOptions(headers: headers));

        // Neither field is null/empty by default — MessageBus always auto-assigns a CorrelationId,
        // and an untenanted context reads back Wolverine's internal default-tenant sentinel rather
        // than null. The meaningful assertion is that neither header's specific value won.
        reply.CorrelationId.ShouldNotBe("corr-should-be-ignored");
        reply.TenantId.ShouldNotBe("tenant-should-be-ignored");
    }
}
