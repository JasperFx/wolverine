using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System.ServiceModel;
using Wolverine.Attributes;
using Xunit;

namespace Wolverine.Grpc.Tests.HandWrittenChain;

[Collection("grpc-hand-written-chain")]
public class hand_written_chain_integration_tests : IClassFixture<HandWrittenChainFixture>
{
    private readonly HandWrittenChainFixture _fixture;

    public hand_written_chain_integration_tests(HandWrittenChainFixture fixture)
    {
        _fixture = fixture;
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
            yield return item;
        await Task.CompletedTask;
    }

    // --- Codegen shape ---

    [Fact]
    public void generated_wrapper_type_follows_grpc_handler_naming_convention()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.HandWrittenChains.Single(c => c.ServiceClassType == typeof(HandWrittenTestGrpcService));

        chain.TypeName.ShouldBe("HandWrittenTestGrpcHandler");
        chain.GeneratedType.ShouldNotBeNull();
        chain.GeneratedType!.Name.ShouldBe("HandWrittenTestGrpcHandler");
    }

    [Fact]
    public void generated_wrapper_implements_the_service_contract_interface()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.HandWrittenChains.Single(c => c.ServiceClassType == typeof(HandWrittenTestGrpcService));

        chain.GeneratedType.ShouldNotBeNull();
        chain.GeneratedType!.GetInterfaces().ShouldContain(typeof(IHandWrittenTestService));
    }

    [Fact]
    public void all_three_rpc_shapes_are_classified_correctly()
    {
        var methods = HandWrittenGrpcServiceChain.DiscoverMethods(typeof(IHandWrittenTestService))
            .ToDictionary(m => m.Method.Name, m => m.Kind);

        methods[nameof(IHandWrittenTestService.Echo)].ShouldBe(HandWrittenMethodKind.Unary);
        methods[nameof(IHandWrittenTestService.EchoStream)].ShouldBe(HandWrittenMethodKind.ServerStreaming);
        methods[nameof(IHandWrittenTestService.EchoBidi)].ShouldBe(HandWrittenMethodKind.BidirectionalStreaming);
    }

    // --- Unary delegation ---

    [Fact]
    public async Task unary_call_delegates_to_inner_service_and_returns_response()
    {
        var client = _fixture.CreateClient<IHandWrittenTestService>();

        var reply = await client.Echo(new HandWrittenTestRequest { Text = "hello" });

        reply.Echo.ShouldBe("hello");
    }

    // --- Server-streaming delegation ---

    [Fact]
    public async Task server_streaming_call_delegates_to_inner_service()
    {
        var client = _fixture.CreateClient<IHandWrittenTestService>();

        var replies = new List<string>();
        await foreach (var reply in client.EchoStream(new HandWrittenTestStreamRequest { Text = "item", Count = 3 }))
            replies.Add(reply.Echo);

        replies.ShouldBe(["item:0", "item:1", "item:2"]);
    }

    // --- Bidi streaming delegation ---

    [Fact]
    public async Task bidi_streaming_call_delegates_to_inner_service()
    {
        var client = _fixture.CreateClient<IHandWrittenTestService>();

        var requests = ToAsyncEnumerable([
            new HandWrittenTestRequest { Text = "a" },
            new HandWrittenTestRequest { Text = "b" },
            new HandWrittenTestRequest { Text = "c" }
        ]);
        var replies = new List<string>();
        await foreach (var reply in client.EchoBidi(requests))
            replies.Add(reply.Echo);

        replies.ShouldBe(["a", "b", "c"]);
    }

    // --- Validate short-circuit ---

    [Fact]
    public async Task valid_request_passes_through_to_inner_service()
    {
        var client = _fixture.CreateClient<IHandWrittenTestService>();

        var reply = await client.Echo(new HandWrittenTestRequest { Text = "valid" });

        reply.Echo.ShouldBe("valid");
    }

    [Fact]
    public async Task empty_text_is_rejected_by_validate_before_inner_service_runs()
    {
        var client = _fixture.CreateClient<IHandWrittenTestService>();

        var ex = await Should.ThrowAsync<RpcException>(
            async () => await client.Echo(new HandWrittenTestRequest { Text = "" }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldBe("Text is required");
    }

    [Fact]
    public async Task validate_does_not_apply_to_bidi_streaming_method()
    {
        // Validate frames are not woven for bidi — the wrapper delegates without short-circuiting.
        var client = _fixture.CreateClient<IHandWrittenTestService>();

        var requests = ToAsyncEnumerable([new HandWrittenTestRequest { Text = "x" }]);
        var replies = new List<string>();
        await foreach (var reply in client.EchoBidi(requests))
            replies.Add(reply.Echo);

        replies.ShouldBe(["x"]);
    }
}

[Collection("grpc-hand-written-chain-unit")]
public class hand_written_chain_discovery_tests
{
    [Fact]
    public void find_hand_written_service_classes_discovers_the_test_service()
    {
        var found = GrpcGraph.FindHandWrittenServiceClasses([typeof(HandWrittenTestGrpcService).Assembly])
            .ToList();

        found.ShouldContain(typeof(HandWrittenTestGrpcService));
    }

    [Fact]
    public void find_hand_written_service_classes_excludes_abstract_types()
    {
        var found = GrpcGraph.FindHandWrittenServiceClasses([typeof(HandWrittenTestGrpcService).Assembly])
            .ToList();

        found.ShouldAllBe(t => !t.IsAbstract);
    }

    [Fact]
    public void resolve_type_name_strips_grpc_service_suffix()
    {
        HandWrittenGrpcServiceChain.ResolveTypeName(typeof(HandWrittenTestGrpcService))
            .ShouldBe("HandWrittenTestGrpcHandler");
    }

    [Fact]
    public void resolve_type_name_appends_grpc_handler_when_no_suffix_present()
    {
        HandWrittenGrpcServiceChain.ResolveTypeName(typeof(NoSuffixServiceStub))
            .ShouldBe("NoSuffixServiceStubGrpcHandler");
    }

    [Fact]
    public void find_service_contract_interface_returns_service_contract_annotated_interface()
    {
        var contract = HandWrittenGrpcServiceChain.FindServiceContractInterface(typeof(HandWrittenTestGrpcService));

        contract.ShouldBe(typeof(IHandWrittenTestService));
    }

    [Fact]
    public void find_hand_written_service_classes_excludes_classes_whose_interface_carries_wolverine_grpc_service()
    {
        // CodeFirstGrpcServiceChain owns contracts where the interface itself has [WolverineGrpcService].
        var found = GrpcGraph.FindHandWrittenServiceClasses([typeof(HandWrittenTestGrpcService).Assembly])
            .ToList();

        found.ShouldNotContain(t => t == typeof(ShouldBeExcludedBecauseInterfaceIsAnnotated));
    }

    [Fact]
    public void chain_scoping_is_grpc_so_middleware_routes_only_to_grpc_chains()
    {
        // MiddlewareScoping.Grpc is what prevents handler-bus middleware from leaking into gRPC
        // chains and gRPC middleware from leaking into handler chains.
        var chain = new HandWrittenGrpcServiceChain(typeof(HandWrittenTestGrpcService));
        chain.Scoping.ShouldBe(MiddlewareScoping.Grpc);
    }
}

// --- Helper stubs for unit tests only ---
// Plain types with no GrpcService suffix and no [WolverineGrpcService] are not picked up by the
// full discovery scan, so these do not interfere with the fixture's chain list.

public class NoSuffixServiceStub { }

[ServiceContract]
[WolverineGrpcService]
public interface IAnnotatedContract { }

public class ShouldBeExcludedBecauseInterfaceIsAnnotated : IAnnotatedContract { }
