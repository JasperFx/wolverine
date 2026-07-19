using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc;
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
        await cts.CancelAsync();

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
                if (received == 2) await cts.CancelAsync();
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
    public void all_rpc_methods_are_classified_correctly()
    {
        var methods = CodeFirstGrpcServiceChain.DiscoverMethods(typeof(ICodeFirstTestService)).ToArray();

        methods.Length.ShouldBe(3);
        methods.Single(m => m.Method.Name == nameof(ICodeFirstTestService.Echo)).Kind
            .ShouldBe(CodeFirstMethodKind.Unary);
        methods.Single(m => m.Method.Name == nameof(ICodeFirstTestService.EchoStream)).Kind
            .ShouldBe(CodeFirstMethodKind.ServerStreaming);
        methods.Single(m => m.Method.Name == nameof(ICodeFirstTestService.SumStream)).Kind
            .ShouldBe(CodeFirstMethodKind.ClientStreaming);
    }

    [Fact]
    public async Task round_trip_client_streaming_through_generated_implementation()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();

        var reply = await client.SumStream(Numbers(1, 2, 3));

        reply.Total.ShouldBe(6);
        reply.Count.ShouldBe(3);
    }

    [Fact]
    public async Task empty_client_stream_still_produces_a_reply()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();

        var reply = await client.SumStream(Numbers());

        reply.Total.ShouldBe(0);
        reply.Count.ShouldBe(0);
    }

    [Fact]
    public async Task mid_drain_handler_fault_surfaces_as_failed_precondition()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();

        var ex = await Should.ThrowAsync<RpcException>(() =>
            client.SumStream(Numbers(1, SumStreamHandler.PoisonValue, 3)));

        ex.StatusCode.ShouldBe(StatusCode.FailedPrecondition);
    }

    [Fact]
    public async Task cancelling_the_client_stream_aborts_the_call()
    {
        var client = _fixture.CreateClient<ICodeFirstTestService>();
        using var cts = new CancellationTokenSource();
        var ctx = new CallContext(new CallOptions(cancellationToken: cts.Token));

        async IAsyncEnumerable<CodeFirstNumber> cancelAfterTwo()
        {
            yield return new CodeFirstNumber { Value = 1 };
            yield return new CodeFirstNumber { Value = 2 };
            await cts.CancelAsync();
            // Block on the cancelled token rather than yielding more items — cancellation,
            // not stream completion, must be what ends the call.
            await Task.Delay(Timeout.Infinite, cts.Token);
            yield return new CodeFirstNumber { Value = 3 };
        }

        var ex = await Should.ThrowAsync<Exception>(() => client.SumStream(cancelAfterTwo(), ctx));

        (ex is OperationCanceledException
         || (ex is RpcException rpc && rpc.StatusCode == StatusCode.Cancelled)).ShouldBeTrue(
            $"expected a cancellation-shaped failure but got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public void orphan_client_streaming_contract_still_generates_an_implementation()
    {
        // Construction-succeeds assertion: before the code-first client-streaming support this
        // shape was skipped by the classifier; now the chain must classify and generate it even
        // when no handler exists for the element type (the failure is at call time, not startup).
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ICodeFirstOrphanStreamService));

        chain.SupportedMethods.Single().Kind.ShouldBe(CodeFirstMethodKind.ClientStreaming);
        chain.GeneratedType.ShouldNotBeNull();
    }

    [Fact]
    public async Task client_streaming_without_a_handler_surfaces_unimplemented()
    {
        var client = _fixture.CreateClient<ICodeFirstOrphanStreamService>();

        async IAsyncEnumerable<CodeFirstOrphanNumber> one()
        {
            yield return new CodeFirstOrphanNumber { Value = 1 };
            await Task.Yield();
        }

        // The bus throws NotSupportedException when no handler accepts
        // IAsyncEnumerable<CodeFirstOrphanNumber>; the default exception mapping surfaces
        // that as Unimplemented.
        var ex = await Should.ThrowAsync<RpcException>(() => client.Collect(one()));
        ex.StatusCode.ShouldBe(StatusCode.Unimplemented);
    }

    [Fact]
    public void client_streaming_codegen_forwards_the_stream_without_an_adapter()
    {
        // protobuf-net.Grpc hands the generated method an IAsyncEnumerable<CodeFirstNumber>
        // directly, so — unlike the proto-first wrapper — no stream-reader adapter
        // (WolverineGrpcStreamAdapters.ReadAllAsync) may appear in the generated source.
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(ICodeFirstTestService));

        chain.SourceCode.ShouldNotBeNull();
        chain.SourceCode!.ShouldContain(
            $"StreamAsync<{typeof(CodeFirstNumber).FullName}, {typeof(CodeFirstSumReply).FullName}>");
        chain.SourceCode.ShouldNotContain(nameof(WolverineGrpcStreamAdapters.ReadAllAsync));
    }

    private static async IAsyncEnumerable<CodeFirstNumber> Numbers(params int[] values)
    {
        foreach (var value in values)
        {
            yield return new CodeFirstNumber { Value = value };
            await Task.Yield();
        }
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
    public void application_assemblies_null_before_discovery()
    {
        // ApplicationAssemblies is set by GrpcGraph.DiscoverServices, not by the constructor.
        // Chains constructed directly (unit tests, tooling) must not throw.
        var chain = new CodeFirstGrpcServiceChain(typeof(ICodeFirstValidatedService));

        chain.ApplicationAssemblies.ShouldBeNull();
    }

    [Fact]
    public void client_streaming_shape_is_classified_with_and_without_call_context()
    {
        var methods = CodeFirstGrpcServiceChain.DiscoverMethods(typeof(IStreamShapesContract)).ToArray();

        methods.Single(m => m.Method.Name == nameof(IStreamShapesContract.Fold)).Kind
            .ShouldBe(CodeFirstMethodKind.ClientStreaming);
        methods.Single(m => m.Method.Name == nameof(IStreamShapesContract.FoldNoContext)).Kind
            .ShouldBe(CodeFirstMethodKind.ClientStreaming);
    }

    [Fact]
    public void bidi_shape_is_skipped_not_misclassified()
    {
        // IAsyncEnumerable<TResponse> Name(IAsyncEnumerable<TRequest>) is the bidirectional
        // shape — not generated on the code-first path. It must fall out of discovery entirely
        // rather than being misclassified as client- or server-streaming.
        var methods = CodeFirstGrpcServiceChain.DiscoverMethods(typeof(IStreamShapesContract)).ToArray();

        methods.ShouldNotContain(m => m.Method.Name == nameof(IStreamShapesContract.EchoAll));
        methods.Length.ShouldBe(2);
    }

    [Fact]
    public void validate_method_on_handler_is_discovered_via_assembly_scan()
    {
        // SubmitHandler.Validate lives on the handler class for CodeFirstValidateRequest.
        // Confirm the assembly scan will find it and that it has the expected static shape.
        var validateMethod = typeof(SubmitHandler).GetMethod(nameof(SubmitHandler.Validate))!;

        validateMethod.ShouldNotBeNull();
        validateMethod.IsStatic.ShouldBeTrue();
        validateMethod.ReturnType.ShouldBe(typeof(Status?));
    }
}

// --- helpers for unit tests ---
// These types deliberately omit [ServiceContract] so they are NOT picked up by the fixture's
// assembly scan (FindCodeFirstServiceContracts requires both [ServiceContract] and [WolverineGrpcService]).
// The unit tests exercise the underlying methods directly rather than via the full pipeline.

// A type whose name starts with 'I' followed by lowercase — the leading 'I' must NOT be stripped.
[WolverineGrpcService]
public interface ImaginaryServiceContract { }

// Streamed-request shapes for the classification unit tests: two valid client-streaming
// variants (with and without CallContext) and the bidi shape that must stay skipped.
[WolverineGrpcService]
public interface IStreamShapesContract
{
    Task<CodeFirstReply> Fold(IAsyncEnumerable<CodeFirstRequest> requests, CallContext context = default);
    Task<CodeFirstReply> FoldNoContext(IAsyncEnumerable<CodeFirstRequest> requests);
    IAsyncEnumerable<CodeFirstReply> EchoAll(IAsyncEnumerable<CodeFirstRequest> requests, CallContext context = default);
}

// Simulates a user who mistakenly marks BOTH the interface and its concrete implementation
// with [WolverineGrpcService] — the conflict guard must fire when called directly.
[WolverineGrpcService]
public interface IConflictingService { }

[WolverineGrpcService]
public class ConflictingServiceImpl : IConflictingService { }
