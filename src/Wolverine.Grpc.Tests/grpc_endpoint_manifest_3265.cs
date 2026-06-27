using GreeterCodeFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Grpc.Tests.GrpcBidiStreaming;
using Wolverine.Grpc.Tests.GrpcBidiStreaming.Generated;
using Wolverine.Grpc.Tests.HandWrittenChain;
using Xunit;
using ProtoStreamGreetingsRequest = GreeterProtoFirstGrpc.Messages.StreamGreetingsRequest;
using CodeFirstStreamGreetingsRequest = GreeterCodeFirstGrpc.Messages.StreamGreetingsRequest;

namespace Wolverine.Grpc.Tests;

// GH-3265: comprehensive coverage of IGrpcEndpointManifest across every gRPC endpoint configuration Wolverine
// supports, so CritterWatch can populate PublisherKind.GrpcEndpoint for each message-publishing origin.
//
// The manifest surfaces every RPC kind whose Wolverine-generated wrapper forwards the request to the message bus:
//   * proto-first  — unary (InvokeAsync), server-streaming (StreamAsync), bidirectional-streaming (StreamAsync)
//   * code-first   — unary (InvokeAsync), server-streaming (StreamAsync)
// and deliberately EXCLUDES the two discovery modes Wolverine does not forward to the bus:
//   * hand-written — the generated wrapper delegates to the user's own service class
//   * direct-mapped — mapped with no Wolverine chain at all
// For those two there is no reliable message-publishing origin, so they must never appear in the manifest.

/// <summary>
///     Shared host discovering the proto-first <c>Greeter</c> sample (unary + server-streaming) and the code-first
///     <c>IGreeterCodeFirstService</c> contract (unary + server-streaming). Built once for the whole class so the
///     per-fact assertions stay fast and isolated from the other gRPC services living in the test assembly.
/// </summary>
public sealed class GreeterManifestFixture : IAsyncLifetime
{
    private IHost? _host;

    public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; private set; } = [];

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Proto-first Greeter stub lives in the GreeterProtoFirstGrpc.Server assembly.
                opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                // Pull in the code-first contract assembly so it is discovered too.
                opts.Discovery.IncludeAssembly(typeof(IGreeterCodeFirstService).Assembly);
            })
            .ConfigureServices(services => services.AddWolverineGrpc())
            .StartAsync();

        // Reading Endpoints self-triggers discovery (no MapWolverineGrpcServices required).
        Endpoints = _host.Services.GetRequiredService<IGrpcEndpointManifest>().Endpoints;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}

[Collection(GrpcSerialTestsCollection.Name)]
public class grpc_endpoint_manifest_3265 : IClassFixture<GreeterManifestFixture>
{
    private readonly IReadOnlyList<GrpcEndpointDescriptor> _endpoints;

    public grpc_endpoint_manifest_3265(GreeterManifestFixture fixture)
    {
        _endpoints = fixture.Endpoints;
    }

    private GrpcEndpointDescriptor protoFirst(string methodName)
        => _endpoints.Single(e => e.Mode == GrpcServiceDiscoveryMode.ProtoFirst && e.MethodName == methodName);

    private GrpcEndpointDescriptor codeFirst(string methodName)
        => _endpoints.Single(e => e.Mode == GrpcServiceDiscoveryMode.CodeFirst && e.MethodName == methodName);

    [Fact]
    public void manifest_is_populated()
    {
        _endpoints.ShouldNotBeEmpty();
    }

    // --- proto-first: unary ----------------------------------------------------------------------------------------

    [Fact]
    public void proto_first_unary_say_hello()
    {
        var e = protoFirst("SayHello");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.Unary);
        e.ServiceName.ShouldBe("Greeter");
        e.RequestType.ShouldBe(typeof(HelloRequest));
        e.ResponseType.ShouldBe(typeof(HelloReply));
        e.HandlerType.ShouldBe(typeof(GreeterGrpcService));
    }

    [Fact]
    public void proto_first_unary_say_goodbye()
    {
        var e = protoFirst("SayGoodbye");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.Unary);
        e.ServiceName.ShouldBe("Greeter");
        e.RequestType.ShouldBe(typeof(GoodbyeRequest));
        e.ResponseType.ShouldBe(typeof(GoodbyeReply));
        e.HandlerType.ShouldBe(typeof(GreeterGrpcService));
    }

    [Fact]
    public void proto_first_unary_fault()
    {
        // The Fault RPC is unary too — it forwards FaultRequest to the bus like any other unary method.
        var e = protoFirst("Fault");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.Unary);
        e.RequestType.ShouldBe(typeof(FaultRequest));
        e.ResponseType.ShouldBe(typeof(FaultReply));
    }

    // --- proto-first: server-streaming -----------------------------------------------------------------------------

    [Fact]
    public void proto_first_server_streaming_stream_greetings()
    {
        var e = protoFirst("StreamGreetings");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.ServerStreaming);
        e.ServiceName.ShouldBe("Greeter");
        // The request is the single request DTO forwarded to StreamAsync.
        e.RequestType.ShouldBe(typeof(ProtoStreamGreetingsRequest));
        // The response is the element type of the outbound IServerStreamWriter<HelloReply>, NOT the writer itself.
        e.ResponseType.ShouldBe(typeof(HelloReply));
        e.HandlerType.ShouldBe(typeof(GreeterGrpcService));
    }

    // --- code-first: unary -----------------------------------------------------------------------------------------

    [Fact]
    public void code_first_unary_greet()
    {
        var e = codeFirst("Greet");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.Unary);
        e.ServiceName.ShouldBe("GreeterCodeFirstService");
        e.RequestType.ShouldBe(typeof(GreetRequest));
        e.ResponseType.ShouldBe(typeof(GreetReply));
        e.HandlerType.ShouldBe(typeof(IGreeterCodeFirstService));
    }

    // --- code-first: server-streaming ------------------------------------------------------------------------------

    [Fact]
    public void code_first_server_streaming_stream_greetings()
    {
        var e = codeFirst("StreamGreetings");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.ServerStreaming);
        e.ServiceName.ShouldBe("GreeterCodeFirstService");
        e.RequestType.ShouldBe(typeof(CodeFirstStreamGreetingsRequest));
        // Code-first server-streaming returns IAsyncEnumerable<GreetReply>; the response is its element type.
        e.ResponseType.ShouldBe(typeof(GreetReply));
        e.HandlerType.ShouldBe(typeof(IGreeterCodeFirstService));
    }

    // --- invariants across the whole manifest ----------------------------------------------------------------------

    [Fact]
    public void every_descriptor_is_a_bus_forwarding_mode()
    {
        _endpoints.ShouldAllBe(e =>
            e.Mode == GrpcServiceDiscoveryMode.ProtoFirst || e.Mode == GrpcServiceDiscoveryMode.CodeFirst);
    }

    [Fact]
    public void every_descriptor_has_a_request_message()
    {
        // RequestType is the published Wolverine message — it must always be present.
        _endpoints.ShouldAllBe(e => e.RequestType != null);
    }

    [Fact]
    public void every_descriptor_has_service_and_method_names()
    {
        _endpoints.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.ServiceName) && !string.IsNullOrWhiteSpace(e.MethodName));
    }

    [Fact]
    public void greeter_host_has_no_bidirectional_streaming()
    {
        // The Greeter contracts declare no bidi RPC, so none should be surfaced here (bidi is covered separately).
        _endpoints.ShouldNotContain(e => e.StreamKind == GrpcRpcStreamKind.BidirectionalStreaming);
    }

    [Fact]
    public void stream_greetings_is_surfaced_once_per_discovery_mode()
    {
        // Both the proto-first and code-first contracts declare a StreamGreetings RPC; each is its own origin.
        var streamGreetings = _endpoints.Where(e => e.MethodName == "StreamGreetings").ToArray();
        streamGreetings.Length.ShouldBe(2);
        streamGreetings.ShouldAllBe(e => e.StreamKind == GrpcRpcStreamKind.ServerStreaming);
        streamGreetings.Select(e => e.Mode).OrderBy(m => m)
            .ShouldBe([GrpcServiceDiscoveryMode.ProtoFirst, GrpcServiceDiscoveryMode.CodeFirst]);
    }
}

/// <summary>
///     Covers the two configurations that share the test assembly host: the proto-first bidirectional-streaming
///     wrapper (surfaced) and the hand-written / direct-mapped services (excluded). Reuses the live
///     <see cref="BidiStreamingFixture"/> host, which discovers the entire test assembly — a realistic
///     "many gRPC configurations in one application" surface.
/// </summary>
public class grpc_endpoint_manifest_3265_bidi_and_exclusions : IClassFixture<BidiStreamingFixture>
{
    private readonly BidiStreamingFixture _fixture;

    public grpc_endpoint_manifest_3265_bidi_and_exclusions(BidiStreamingFixture fixture)
    {
        _fixture = fixture;
    }

    private IReadOnlyList<GrpcEndpointDescriptor> Endpoints
        => _fixture.Services.GetRequiredService<IGrpcEndpointManifest>().Endpoints;

    [Fact]
    public void proto_first_bidirectional_streaming_echo_is_surfaced()
    {
        var echo = Endpoints.Single(e =>
            e.Mode == GrpcServiceDiscoveryMode.ProtoFirst
            && e.ServiceName == "BidiEchoTest"
            && e.MethodName == "Echo");

        echo.StreamKind.ShouldBe(GrpcRpcStreamKind.BidirectionalStreaming);
        // The published message is the per-item element type of the inbound request stream — EchoRequest — NOT the
        // IAsyncStreamReader<EchoRequest> wrapper. This guards the bidi request-type unwrap.
        echo.RequestType.ShouldBe(typeof(EchoRequest));
        // The response is the element type of the outbound IServerStreamWriter<EchoReply>.
        echo.ResponseType.ShouldBe(typeof(EchoReply));
        echo.HandlerType.ShouldBe(typeof(BidiEchoStub));
    }

    [Fact]
    public void hand_written_services_are_discovered_but_excluded_from_the_manifest()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();

        // Sanity: the hand-written service IS discovered as a hand-written chain (so the exclusion below is a
        // deliberate manifest decision, not merely the absence of discovery).
        graph.HandWrittenChains.ShouldContain(c => c.ServiceClassType == typeof(HandWrittenTestGrpcService));

        // ...yet none of its methods (Echo / EchoStream / EchoBidi) leak into the message-origin manifest, because
        // Wolverine delegates them to the user's own implementation rather than forwarding to the bus.
        Endpoints.ShouldNotContain(e => e.HandlerType == typeof(HandWrittenTestGrpcService));
        Endpoints.ShouldNotContain(e => e.HandlerType == typeof(IHandWrittenTestService));
    }

    [Fact]
    public void manifest_never_surfaces_hand_written_or_direct_mapped_modes()
    {
        // Even in an application packed with every discovery flavor, the manifest only ever reports the two
        // bus-forwarding modes. Hand-written and direct-mapped have no message-publishing origin.
        Endpoints.ShouldAllBe(e =>
            e.Mode == GrpcServiceDiscoveryMode.ProtoFirst || e.Mode == GrpcServiceDiscoveryMode.CodeFirst);
    }

    [Fact]
    public void every_surfaced_endpoint_carries_a_request_message_and_known_stream_kind()
    {
        Endpoints.ShouldAllBe(e =>
            e.RequestType != null
            && (e.StreamKind == GrpcRpcStreamKind.Unary
                || e.StreamKind == GrpcRpcStreamKind.ServerStreaming
                || e.StreamKind == GrpcRpcStreamKind.BidirectionalStreaming));
    }
}
