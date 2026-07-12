using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc;
using Shouldly;
using Wolverine.Grpc.Tests.ServerPropagation;
using Xunit;

namespace Wolverine.Grpc.Tests.MultiTenancy;

/// <summary>
///     Structural tests for GH-3368: proves the tenant detection frame lands in the generated
///     source of all three gRPC chain flavors, and that the zero-config default is applied (or
///     suppressed) exactly as documented. These assertions are on the generated code itself, so
///     they cannot be satisfied by the runtime propagation interceptor — which is the whole point
///     of the codegen-level detection.
/// </summary>
[Collection("grpc-tenant-detection")]
public class tenant_detection_codegen_tests
{
    [Fact]
    public async Task zero_config_default_weaves_detection_into_all_three_chain_flavors()
    {
        // The test assembly conveniently contains all three flavors: proto-first stubs
        // (GreeterMiddlewareTestStub, ValidateGreeterStub), code-first contracts
        // (ITenantEchoService, ICodeFirstTestService...), and hand-written service classes
        // (PropagationEchoGrpcService).
        await using var host = await TenantDetectionHost.StartAsync();

        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var options = host.Services.GetRequiredService<WolverineGrpcOptions>();

        options.TenantIdDetection.ZeroConfigDefaultApplied.ShouldBeTrue();

        graph.Chains.ShouldNotBeEmpty();
        foreach (var chain in graph.Chains)
        {
            chain.SourceCode.ShouldNotBeNull();
            chain.SourceCode!.ShouldContain("TryDetectTenantIdAsync");
            chain.SourceCode.ShouldContain("var tenantId = await");
        }

        graph.CodeFirstChains.ShouldNotBeEmpty();
        var echoChain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ITenantEchoService));
        echoChain.SourceCode.ShouldNotBeNull();
        echoChain.SourceCode!.ShouldContain("TryDetectTenantIdAsync");
        // The detected value must land on the wrapper's scoped bus so InvokeAsync carries it
        // onto the envelope — structurally, without relying on the ambient IMessageContext.
        echoChain.SourceCode.ShouldContain(".TenantId = tenantId");

        graph.HandWrittenChains.ShouldNotBeEmpty();
        var handWritten = graph.HandWrittenChains.Single(c => c.ServiceClassType == typeof(PropagationEchoGrpcService));
        handWritten.SourceCode.ShouldNotBeNull();
        handWritten.SourceCode!.ShouldContain("TryDetectTenantIdAsync");
        // Hand-written delegation wrappers have no bus field — the tenant is applied to the
        // request-scoped IMessageContext instead.
        handWritten.SourceCode.ShouldContain("TryApplyToAmbientContext");
    }

    [Fact]
    public async Task detection_declares_the_persistence_tenant_id_codegen_variable()
    {
        await using var host = await TenantDetectionHost.StartAsync();

        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var echoChain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ITenantEchoService));

        // PersistenceConstants.TenantIdVariableName is 'tenantId' — the exact variable name that
        // Marten's OpenMartenSessionFrame and Polecat's OpenPolecatSessionFrame look up via
        // TryFindVariableByName to open tenant-scoped sessions.
        echoChain.SourceCode!.ShouldContain(
            $"var {Wolverine.Persistence.PersistenceConstants.TenantIdVariableName} = await");
    }

    [Fact]
    public async Task disabling_envelope_header_propagation_suppresses_the_zero_config_default()
    {
        await using var host = await TenantDetectionHost.StartAsync(o => o.PropagateEnvelopeHeaders = false);

        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var options = host.Services.GetRequiredService<WolverineGrpcOptions>();

        options.TenantIdDetection.ZeroConfigDefaultApplied.ShouldBeFalse();
        options.TenantIdDetection.Strategies.ShouldBeEmpty();

        var echoChain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ITenantEchoService));
        echoChain.SourceCode!.ShouldNotContain("TryDetectTenantIdAsync");
    }

    [Fact]
    public async Task explicit_detection_works_even_with_the_propagation_interceptor_disabled()
    {
        // Proves the codegen path is fully independent of the runtime interceptor: with
        // PropagateEnvelopeHeaders off, only the generated detection frame can move a header
        // value onto the handler's IMessageContext.
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.PropagateEnvelopeHeaders = false;
            o.TenantId.IsRequestHeaderValue("x-tenant");
        });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest(),
            new CallContext(new CallOptions(headers: new Metadata { { "x-tenant", "structural" } })));

        reply.TenantId.ShouldBe("structural");
    }

    [Fact]
    public async Task hand_written_service_detection_reaches_the_ambient_context_end_to_end()
    {
        // PropagationEchoGrpcService is the hand-written flavor (concrete class + [ServiceContract]
        // interface without [WolverineGrpcService]); the generated delegation wrapper applies the
        // detected tenant to the request-scoped IMessageContext, which the user's service (and the
        // bus it resolves) observes. 'x-tenant' is unknown to the propagation interceptor, so only
        // the woven detection frame can make this pass.
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
        });

        var client = host.CreateClient<IPropagationEchoService>();

        var reply = await client.Echo(new PropagationEchoRequest(),
            new CallContext(new CallOptions(headers: new Metadata { { "x-tenant", "hand-written-tenant" } })));

        reply.TenantId.ShouldBe("hand-written-tenant");
    }
}
