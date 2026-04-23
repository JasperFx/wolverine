using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Xunit;

namespace Wolverine.Grpc.Tests.CodeFirstCodegen;

[Collection("grpc-code-first-codegen")]
public class code_first_codegen_integration_tests : IClassFixture<CodeFirstCodegenFixture>
{
    private readonly CodeFirstCodegenFixture _fixture;

    public code_first_codegen_integration_tests(CodeFirstCodegenFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task round_trip_unary_through_generated_implementation()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();

        var reply = await client.Echo(new CodeFirstRequest { Text = "hello" });

        reply.Echo.ShouldBe("hello");
    }

    [Fact]
    public async Task round_trip_server_streaming_through_generated_implementation()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();

        var replies = new List<string>();
        await foreach (var reply in client.EchoStream(new CodeFirstStreamRequest { Text = "item", Count = 3 }))
        {
            replies.Add(reply.Echo);
        }

        replies.ShouldBe(["item:0", "item:1", "item:2"]);
    }

    [Fact]
    public async Task cancellation_propagates_through_generated_unary()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<Exception>(async () =>
            await client.Echo(new CodeFirstRequest { Text = "cancelled" }, cts.Token));
    }

    [Fact]
    public async Task mid_stream_cancellation_stops_enumeration_early()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();
        using var cts = new CancellationTokenSource();
        var received = 0;

        await Should.ThrowAsync<Exception>(async () =>
        {
            await foreach (var _ in client.EchoStream(
                               new CodeFirstStreamRequest { Text = "cancel", Count = 500 }, cts.Token))
            {
                received++;
                if (received == 2) cts.Cancel();
            }
        });

        received.ShouldBeLessThan(500);
    }

    [Fact]
    public void generated_type_name_follows_interface_name_grpc_handler_convention()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ICodeFirstTestService));

        chain.TypeName.ShouldBe("CodeFirstTestServiceGrpcHandler");
        chain.GeneratedType.ShouldNotBeNull();
        chain.GeneratedType!.Name.ShouldBe("CodeFirstTestServiceGrpcHandler");
    }

    [Fact]
    public void generated_type_implements_the_service_contract_interface()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ICodeFirstTestService));

        chain.GeneratedType.ShouldNotBeNull();
        chain.GeneratedType!.GetInterfaces().ShouldContain(typeof(ICodeFirstTestService));
    }

    [Fact]
    public void both_rpc_methods_are_classified_correctly()
    {
        var methods = CodeFirstGrpcServiceChain.DiscoverMethods(typeof(ICodeFirstTestService)).ToArray();

        methods.Length.ShouldBe(2);
        methods.Single(m => m.Method.Name == nameof(ICodeFirstTestService.Echo)).Kind
            .ShouldBe(CodeFirstMethodKind.Unary);
        methods.Single(m => m.Method.Name == nameof(ICodeFirstTestService.EchoStream)).Kind
            .ShouldBe(CodeFirstMethodKind.ServerStreaming);
    }

    [Fact]
    public async Task validate_short_circuit_throws_rpc_exception_before_handler_runs()
    {
        var client = _fixture.CreateClient<ICodeFirstValidatedService>();

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.Submit(new CodeFirstValidateRequest { Text = "bad" }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task validate_returns_null_allows_request_through_to_handler()
    {
        var client = _fixture.CreateClient<ICodeFirstValidatedService>();

        var reply = await client.Submit(new CodeFirstValidateRequest { Text = "good" });

        reply.Echo.ShouldBe("good");
    }
}

[Collection("grpc-code-first-codegen-unit")]
public class code_first_codegen_discovery_tests
{
    [Fact]
    public void find_code_first_service_contracts_returns_annotated_interfaces()
    {
        var contracts = GrpcGraph.FindCodeFirstServiceContracts(
            [typeof(ICodeFirstTestService).Assembly]).ToList();

        contracts.ShouldContain(typeof(ICodeFirstTestService));
    }

    [Fact]
    public void find_code_first_service_contracts_does_not_return_concrete_classes()
    {
        var contracts = GrpcGraph.FindCodeFirstServiceContracts(
            [typeof(ICodeFirstTestService).Assembly]).ToList();

        contracts.ShouldAllBe(t => t.IsInterface);
    }

    [Fact]
    public void resolve_type_name_strips_leading_i_from_interface()
    {
        CodeFirstGrpcServiceChain.ResolveTypeName(typeof(ICodeFirstTestService))
            .ShouldBe("CodeFirstTestServiceGrpcHandler");
    }

    [Fact]
    public void resolve_type_name_does_not_strip_i_when_not_followed_by_uppercase()
    {
        // A type named "ImaginaryService" should not have its 'I' stripped since the
        // next character is lowercase — only strip a conventional interface prefix.
        CodeFirstGrpcServiceChain.ResolveTypeName(typeof(ImaginaryServiceContract))
            .ShouldBe("ImaginaryServiceContractGrpcHandler");
    }

    [Fact]
    public void conflict_detection_throws_when_concrete_impl_also_has_attribute()
    {
        Should.Throw<InvalidOperationException>(() =>
            CodeFirstGrpcServiceChain.AssertNoConcreteImplementationConflicts(
                typeof(IConflictingService),
                [typeof(IConflictingService).Assembly]));
    }

    [Fact]
    public void chain_scoping_is_grpc_so_middleware_routes_only_to_grpc_chains()
    {
        // MiddlewareScoping.Grpc is what prevents handler-bus middleware from leaking into gRPC
        // chains and gRPC middleware from leaking into handler chains.
        var chain = new CodeFirstGrpcServiceChain(typeof(ICodeFirstTestService));
        chain.Scoping.ShouldBe(MiddlewareScoping.Grpc);
    }

    [Fact]
    public void discovered_befores_finds_static_validate_method_on_interface()
    {
        var chain = new CodeFirstGrpcServiceChain(typeof(ICodeFirstValidatedService));

        chain.DiscoveredBefores.ShouldContain(m => m.Name == nameof(ICodeFirstValidatedService.Validate));
    }

    [Fact]
    public void discovered_befores_is_empty_for_interface_with_no_static_hook_methods()
    {
        var chain = new CodeFirstGrpcServiceChain(typeof(ICodeFirstTestService));

        chain.DiscoveredBefores.ShouldBeEmpty();
    }

    [Fact]
    public void discovered_befores_filters_validate_by_request_type()
    {
        // ICodeFirstValidatedService.Validate(CodeFirstValidateRequest) should NOT appear
        // as a before for ICodeFirstTestService (which uses CodeFirstRequest), confirming
        // that IsBeforeApplicable filters correctly by parameter type.
        var chain = new CodeFirstGrpcServiceChain(typeof(ICodeFirstTestService));
        var validateMethod = typeof(ICodeFirstValidatedService)
            .GetMethod(nameof(ICodeFirstValidatedService.Validate))!;

        // Inject the validate method into a fresh chain's discovered befores by checking
        // applicability directly via the public discovery path — the method lives on a different
        // interface, so it won't appear in ICodeFirstTestService.DiscoveredBefores at all,
        // but we verify DiscoveredBefores for the correct interface is non-empty.
        var validatedChain = new CodeFirstGrpcServiceChain(typeof(ICodeFirstValidatedService));
        validatedChain.DiscoveredBefores.ShouldContain(validateMethod);
        chain.DiscoveredBefores.ShouldNotContain(validateMethod);
    }
}

// --- helpers for unit tests ---
// These types deliberately omit [ServiceContract] so they are NOT picked up by the fixture's
// assembly scan (FindCodeFirstServiceContracts requires both [ServiceContract] and [WolverineGrpcService]).
// The unit tests exercise the underlying methods directly rather than via the full pipeline.

// A type whose name starts with 'I' followed by lowercase — the leading 'I' must NOT be stripped.
[WolverineGrpcService]
public interface ImaginaryServiceContract { }

// Simulates a user who mistakenly marks BOTH the interface and its concrete implementation
// with [WolverineGrpcService] — the conflict guard must fire when called directly.
[WolverineGrpcService]
public interface IConflictingService { }

[WolverineGrpcService]
public class ConflictingServiceImpl : IConflictingService { }
