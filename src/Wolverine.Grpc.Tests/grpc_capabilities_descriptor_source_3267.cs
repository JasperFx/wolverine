using GreeterCodeFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Grpc.Tests.GrpcBidiStreaming;
using Wolverine.Grpc.Tests.GrpcBidiStreaming.Generated;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using ProtoStreamGreetingsRequest = GreeterProtoFirstGrpc.Messages.StreamGreetingsRequest;
using CodeFirstStreamGreetingsRequest = GreeterCodeFirstGrpc.Messages.StreamGreetingsRequest;

namespace Wolverine.Grpc.Tests;

// GH-3267: surface the gRPC endpoint manifest (GH-3266) along the ServiceCapabilities descriptor path so a monitoring
// console (CritterWatch) can discover the gRPC services a Wolverine app exposes — parallel to how it discovers HTTP
// chains and ASP.NET endpoints. Wolverine.Grpc registers an IGrpcEndpointDescriptorSource that projects the manifest
// into serializable GrpcRpcDescriptors, and ServiceCapabilities.ReadFrom folds them into ServiceCapabilities.GrpcEndpoints.

/// <summary>
///     Builds a host discovering the proto-first <c>Greeter</c> sample (unary + server-streaming) and the code-first
///     <c>IGreeterCodeFirstService</c> contract (unary + server-streaming), then captures both the raw descriptor
///     source output and the full <see cref="ServiceCapabilities"/> snapshot once for the whole class.
/// </summary>
public sealed class GrpcCapabilitiesFixture : IAsyncLifetime
{
    private IHost? _host;

    public IReadOnlyList<GrpcRpcDescriptor> SourceEndpoints { get; private set; } = [];
    public ServiceCapabilities Capabilities { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                opts.Discovery.IncludeAssembly(typeof(IGreeterCodeFirstService).Assembly);
            })
            .ConfigureServices(services => services.AddWolverineGrpc())
            .StartAsync();

        SourceEndpoints = _host.Services.GetRequiredService<IGrpcEndpointDescriptorSource>().Endpoints;
        Capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null, CancellationToken.None);
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
public class grpc_capabilities_descriptor_source_3267 : IClassFixture<GrpcCapabilitiesFixture>
{
    private readonly GrpcCapabilitiesFixture _fixture;

    public grpc_capabilities_descriptor_source_3267(GrpcCapabilitiesFixture fixture)
    {
        _fixture = fixture;
    }

    private GrpcRpcDescriptor source(GrpcServiceDiscoveryMode mode, string method)
        => _fixture.SourceEndpoints.Single(e => e.Mode == mode && e.MethodName == method);

    [Fact]
    public void source_is_registered_and_populated()
    {
        _fixture.SourceEndpoints.ShouldNotBeEmpty();
    }

    [Fact]
    public void projects_proto_first_unary()
    {
        var e = source(GrpcServiceDiscoveryMode.ProtoFirst, "SayHello");
        e.ServiceName.ShouldBe("Greeter");
        e.MethodName.ShouldBe("SayHello");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.Unary);
        e.Mode.ShouldBe(GrpcServiceDiscoveryMode.ProtoFirst);
        e.RequestType!.FullName.ShouldBe(typeof(HelloRequest).FullName);
        e.ResponseType!.FullName.ShouldBe(typeof(HelloReply).FullName);
        // Origin is the forwarding service identity — the proto-first stub whose generated wrapper dispatches to the bus.
        e.Origin!.FullName.ShouldBe(typeof(GreeterGrpcService).FullName);
        e.Subject.ShouldBe("GrpcEndpoint[Greeter/SayHello]");
        e.Tags.ShouldContain("grpc");
    }

    [Fact]
    public void projects_proto_first_server_streaming()
    {
        var e = source(GrpcServiceDiscoveryMode.ProtoFirst, "StreamGreetings");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.ServerStreaming);
        e.RequestType!.FullName.ShouldBe(typeof(ProtoStreamGreetingsRequest).FullName);
        e.ResponseType!.FullName.ShouldBe(typeof(HelloReply).FullName);
        e.Subject.ShouldBe("GrpcEndpoint[Greeter/StreamGreetings]");
    }

    [Fact]
    public void projects_code_first_unary()
    {
        var e = source(GrpcServiceDiscoveryMode.CodeFirst, "Greet");
        e.ServiceName.ShouldBe("GreeterCodeFirstService");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.Unary);
        e.Mode.ShouldBe(GrpcServiceDiscoveryMode.CodeFirst);
        e.RequestType!.FullName.ShouldBe(typeof(GreetRequest).FullName);
        e.ResponseType!.FullName.ShouldBe(typeof(GreetReply).FullName);
        e.Origin!.FullName.ShouldBe(typeof(IGreeterCodeFirstService).FullName);
    }

    [Fact]
    public void projects_code_first_server_streaming()
    {
        var e = source(GrpcServiceDiscoveryMode.CodeFirst, "StreamGreetings");
        e.StreamKind.ShouldBe(GrpcRpcStreamKind.ServerStreaming);
        e.RequestType!.FullName.ShouldBe(typeof(CodeFirstStreamGreetingsRequest).FullName);
        e.ResponseType!.FullName.ShouldBe(typeof(GreetReply).FullName);
        e.ServiceName.ShouldBe("GreeterCodeFirstService");
    }

    [Fact]
    public void every_descriptor_carries_subject_request_origin_and_grpc_tag()
    {
        _fixture.SourceEndpoints.ShouldAllBe(e =>
            e.Subject.StartsWith("GrpcEndpoint[")
            && e.RequestType != null
            && e.Origin != null
            && e.Tags.Contains("grpc"));
    }

    // --- end-to-end through ServiceCapabilities.ReadFrom ------------------------------------------------------------

    [Fact]
    public void capabilities_grpc_endpoints_is_populated_from_the_source()
    {
        // The descriptor source is folded into ServiceCapabilities.GrpcEndpoints by ReadFrom.
        _fixture.Capabilities.GrpcEndpoints.Count.ShouldBe(_fixture.SourceEndpoints.Count);
        _fixture.Capabilities.GrpcEndpoints.ShouldContain(e =>
            e.ServiceName == "Greeter" && e.MethodName == "SayHello");
        _fixture.Capabilities.GrpcEndpoints.ShouldContain(e =>
            e.ServiceName == "GreeterCodeFirstService" && e.MethodName == "Greet");
    }

    [Fact]
    public void capabilities_grpc_endpoints_are_ordered_by_service_then_method()
    {
        var keys = _fixture.Capabilities.GrpcEndpoints
            .Select(e => e.ServiceName + "::" + e.MethodName)
            .ToArray();

        keys.ShouldBe(keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>
///     Covers the bidirectional-streaming origin (reusing the live <see cref="BidiStreamingFixture"/> host) and the
///     no-gRPC case, both through the real <see cref="ServiceCapabilities.ReadFrom"/> path.
/// </summary>
public class grpc_capabilities_3267_bidi_and_absent
{
    public class with_bidi : IClassFixture<BidiStreamingFixture>
    {
        private readonly BidiStreamingFixture _fixture;

        public with_bidi(BidiStreamingFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task capabilities_include_the_bidirectional_streaming_origin()
        {
            var runtime = _fixture.Services.GetRequiredService<IWolverineRuntime>();
            var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);

            var echo = capabilities.GrpcEndpoints.Single(e =>
                e.ServiceName == "BidiEchoTest" && e.MethodName == "Echo");

            echo.StreamKind.ShouldBe(GrpcRpcStreamKind.BidirectionalStreaming);
            echo.Mode.ShouldBe(GrpcServiceDiscoveryMode.ProtoFirst);
            // The published message is the per-item element type of the inbound request stream.
            echo.RequestType!.FullName.ShouldBe(typeof(EchoRequest).FullName);
            echo.ResponseType!.FullName.ShouldBe(typeof(EchoReply).FullName);
            echo.Tags.ShouldContain("grpc");
        }

        [Fact]
        public async Task capabilities_include_the_client_streaming_origin()
        {
            var runtime = _fixture.Services.GetRequiredService<IWolverineRuntime>();
            var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);

            var collect = capabilities.GrpcEndpoints.Single(e =>
                e.ServiceName == "CollectTest" && e.MethodName == "Collect");

            collect.StreamKind.ShouldBe(GrpcRpcStreamKind.ClientStreaming);
            collect.Mode.ShouldBe(GrpcServiceDiscoveryMode.ProtoFirst);
            // The surfaced message is the per-item element type of the inbound request stream; the actual bus
            // message is IAsyncEnumerable<NumberRequest>. The response is unwrapped from Task<SumReply>.
            collect.RequestType!.FullName.ShouldBe(typeof(GrpcClientStreaming.Generated.NumberRequest).FullName);
            collect.ResponseType!.FullName.ShouldBe(typeof(GrpcClientStreaming.Generated.SumReply).FullName);
            collect.Tags.ShouldContain("grpc");
        }

        [Fact]
        public async Task capabilities_include_the_code_first_client_streaming_origin()
        {
            var runtime = _fixture.Services.GetRequiredService<IWolverineRuntime>();
            var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);

            var sum = capabilities.GrpcEndpoints.Single(e =>
                e.ServiceName == "CodeFirstTestService" && e.MethodName == "SumStream");

            sum.StreamKind.ShouldBe(GrpcRpcStreamKind.ClientStreaming);
            sum.Mode.ShouldBe(GrpcServiceDiscoveryMode.CodeFirst);
            // The surfaced message is the per-item element type of the streamed request; the actual bus
            // message is IAsyncEnumerable<CodeFirstNumber>. The response is unwrapped from Task<CodeFirstSumReply>.
            sum.RequestType!.FullName.ShouldBe(typeof(CodeFirstCodegen.CodeFirstNumber).FullName);
            sum.ResponseType!.FullName.ShouldBe(typeof(CodeFirstCodegen.CodeFirstSumReply).FullName);
            sum.Tags.ShouldContain("grpc");
        }

        [Fact]
        public async Task capabilities_only_surface_bus_forwarding_modes()
        {
            // Even in an assembly packed with hand-written + direct-mapped services, GrpcEndpoints only reports the
            // bus-forwarding proto-first/code-first origins (inherited from the manifest's exclusion rule).
            var runtime = _fixture.Services.GetRequiredService<IWolverineRuntime>();
            var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);

            capabilities.GrpcEndpoints.ShouldNotBeEmpty();
            capabilities.GrpcEndpoints.ShouldAllBe(e =>
                e.Mode == GrpcServiceDiscoveryMode.ProtoFirst || e.Mode == GrpcServiceDiscoveryMode.CodeFirst);
        }
    }

    [Collection(GrpcSerialTestsCollection.Name)]
    public class without_grpc
    {
        [Fact]
        public async Task no_grpc_means_empty_capabilities_and_no_registered_source()
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine()
                .StartAsync();

            host.Services.GetService<IGrpcEndpointDescriptorSource>().ShouldBeNull();

            var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, CancellationToken.None);
            capabilities.GrpcEndpoints.ShouldBeEmpty();
        }
    }
}
