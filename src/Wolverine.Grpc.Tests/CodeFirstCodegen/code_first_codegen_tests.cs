using Microsoft.Extensions.DependencyInjection;
using Shouldly;
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
