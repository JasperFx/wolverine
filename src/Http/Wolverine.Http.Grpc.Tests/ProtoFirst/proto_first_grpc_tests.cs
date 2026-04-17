using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Http.Grpc.Tests.ProtoFirst;

[Collection("grpc-proto-first")]
public class proto_first_grpc_tests : IClassFixture<ProtoFirstGrpcFixture>
{
    private readonly ProtoFirstGrpcFixture _fixture;

    public proto_first_grpc_tests(ProtoFirstGrpcFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task round_trip_unary_call_through_generated_wrapper()
    {
        var client = new Greeter.GreeterClient(_fixture.Channel);

        var reply = await client.SayHelloAsync(new HelloRequest { Name = "Erik" });

        reply.Message.ShouldBe("Hello, Erik");
    }

    [Fact]
    public async Task multiple_unary_methods_are_each_overridden_and_forwarded()
    {
        var client = new Greeter.GreeterClient(_fixture.Channel);

        var hello = await client.SayHelloAsync(new HelloRequest { Name = "Wolverine" });
        var bye = await client.SayGoodbyeAsync(new GoodbyeRequest { Name = "Wolverine" });

        hello.Message.ShouldBe("Hello, Wolverine");
        bye.Message.ShouldBe("Goodbye, Wolverine");
    }

    [Fact]
    public void generated_wrapper_type_follows_proto_service_name_grpc_handler_convention()
    {
        // Sanity-guard the naming scheme: {ProtoServiceName}GrpcHandler. If this changes,
        // downstream tooling and docs that match on the suffix will need updating.
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.Chains.Single(c => c.StubType == typeof(GreeterGrpcService));

        chain.ProtoServiceName.ShouldBe("Greeter");
        chain.TypeName.ShouldBe("GreeterGrpcHandler");
        chain.GeneratedType!.Name.ShouldBe("GreeterGrpcHandler");
    }

    [Fact]
    public async Task round_trip_server_streaming_call_through_generated_wrapper()
    {
        var client = new Greeter.GreeterClient(_fixture.Channel);

        using var call = client.StreamGreetings(new StreamGreetingsRequest { Name = "Erik", Count = 3 });

        var received = new List<string>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync())
        {
            received.Add(reply.Message);
        }

        received.ShouldBe(["Hello, Erik [0]", "Hello, Erik [1]", "Hello, Erik [2]"]);
    }

    [Fact]
    public async Task mid_stream_cancellation_stops_enumeration_early()
    {
        var client = new Greeter.GreeterClient(_fixture.Channel);
        using var cts = new CancellationTokenSource();

        using var call = client.StreamGreetings(
            new StreamGreetingsRequest { Name = "Erik", Count = 1000 },
            cancellationToken: cts.Token);

        var received = 0;
        await Should.ThrowAsync<Exception>(async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                received++;
                if (received == 2)
                {
                    cts.Cancel();
                }
            }
        });

        received.ShouldBeLessThan(1000);
    }

    [Theory]
    [InlineData("argument", StatusCode.InvalidArgument)]
    [InlineData("key", StatusCode.NotFound)]
    [InlineData("unauthorized", StatusCode.PermissionDenied)]
    [InlineData("invalid", StatusCode.FailedPrecondition)]
    [InlineData("notimpl", StatusCode.Unimplemented)]
    [InlineData("generic", StatusCode.Internal)]
    public async Task unary_handler_exception_maps_to_canonical_status_code(string kind, StatusCode expected)
    {
        var client = new Greeter.GreeterClient(_fixture.Channel);

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.FaultAsync(new FaultRequest { Kind = kind }).ResponseAsync);

        ex.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task unary_call_wolverine_activity_chains_under_aspnetcore_hosting_activity()
    {
        // M6: proto-first generated wrappers route through the same ASP.NET Core gRPC + Wolverine
        // pipeline as code-first, so the handler activity must chain under the same server-side
        // hosting activity. (Client → server header propagation is separately guaranteed by
        // ASP.NET Core + HttpClient on real HTTP/2; TestServer bypasses that layer.)
        using var capture = new WolverineActivityCapture();

        var client = new Greeter.GreeterClient(_fixture.Channel);
        var reply = await client.SayHelloAsync(new HelloRequest { Name = "Erik" });

        reply.Message.ShouldBe("Hello, Erik");
        capture.AssertWolverineActivityChainedUnderServerHostingActivity();
    }

    [Fact]
    public void graph_is_registered_with_options_parts_for_cli_describe()
    {
        // CLI diagnostics ('describe', 'describe-routing') iterate Options.Parts — proto-first
        // services must appear there alongside handlers and HTTP endpoints.
        var runtime = (WolverineRuntime)_fixture.Services.GetRequiredService<IWolverineRuntime>();
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();

        runtime.Options.Parts.ShouldContain(graph);
    }
}

public class proto_first_discovery_tests
{
    [Fact]
    public void discovers_abstract_stub_annotated_with_wolverine_grpc_service_attribute()
    {
        var stubs = GrpcGraph.FindProtoFirstStubs([typeof(GreeterGrpcService).Assembly]).ToList();

        stubs.ShouldContain(typeof(GreeterGrpcService));
    }

    [Fact]
    public void resolves_proto_service_base_from_stub_inheritance()
    {
        var protoBase = GrpcServiceChain.FindProtoServiceBase(typeof(GreeterGrpcService));

        protoBase.ShouldBe(typeof(Greeter.GreeterBase));
    }

    [Fact]
    public void discovers_every_virtual_unary_method_on_the_proto_base()
    {
        var methods = GrpcServiceChain.DiscoverUnaryMethods(typeof(Greeter.GreeterBase))
            .Select(m => m.Name)
            .ToList();

        methods.ShouldContain("SayHello");
        methods.ShouldContain("SayGoodbye");
    }

    [Fact]
    public void classifies_unary_and_server_streaming_methods_distinctly()
    {
        var classified = GrpcServiceChain.DiscoverSupportedMethods(typeof(Greeter.GreeterBase))
            .ToDictionary(m => m.Method.Name, m => m.Kind);

        classified["SayHello"].ShouldBe(GrpcMethodKind.Unary);
        classified["SayGoodbye"].ShouldBe(GrpcMethodKind.Unary);
        classified["StreamGreetings"].ShouldBe(GrpcMethodKind.ServerStreaming);
    }

    [Fact]
    public void discovered_methods_are_sorted_alphabetically_for_byte_stable_codegen()
    {
        // Reflection's GetMethods() order is unspecified; the discovery API promises a
        // stable (ordinal) sort so generated source stays byte-identical across runs.
        var names = GrpcServiceChain.DiscoverSupportedMethods(typeof(Greeter.GreeterBase))
            .Select(m => m.Method.Name)
            .ToList();

        names.ShouldBe(["Fault", "SayGoodbye", "SayHello", "StreamGreetings"]);
    }

    [Fact]
    public void does_not_discover_concrete_code_first_services_as_proto_first_stubs()
    {
        // PingGrpcService is concrete and used in the M3 code-first tests — it must not be
        // picked up by the proto-first discovery path.
        var stubs = GrpcGraph.FindProtoFirstStubs([typeof(PingGrpcService).Assembly]).ToList();

        stubs.ShouldNotContain(typeof(PingGrpcService));
    }

    [Fact]
    public void identifies_concrete_proto_stub_with_wolverine_attribute_as_misuse()
    {
        GrpcGraph.IsConcreteProtoStubMisuse(typeof(MisuseFixtures.ConcreteGreeterStub)).ShouldBeTrue();
    }

    [Fact]
    public void does_not_flag_legitimate_abstract_proto_first_stub_as_misuse()
    {
        GrpcGraph.IsConcreteProtoStubMisuse(typeof(GreeterGrpcService)).ShouldBeFalse();
    }

    [Fact]
    public void does_not_flag_concrete_code_first_service_as_misuse()
    {
        // PingGrpcService is concrete but has no proto base — it's a legitimate code-first service.
        GrpcGraph.IsConcreteProtoStubMisuse(typeof(PingGrpcService)).ShouldBeFalse();
    }

    [Fact]
    public void assertion_throws_with_actionable_message_when_concrete_proto_stub_is_present()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            GrpcGraph.AssertNoConcreteProtoStubs(new[] { typeof(MisuseFixtures.ConcreteGreeterStub) }));

        ex.Message.ShouldContain("abstract");
        // Exception message formats nested-type names with '.' via FullNameInCode(), not the
        // reflection '+' delimiter — assert on the class name directly.
        ex.Message.ShouldContain(nameof(MisuseFixtures.ConcreteGreeterStub));
        ex.Message.ShouldContain("Greeter.GreeterBase");
    }

    [Fact]
    public void assertion_passes_when_only_legitimate_abstract_stubs_are_present()
    {
        Should.NotThrow(() =>
            GrpcGraph.AssertNoConcreteProtoStubs(new[] { typeof(GreeterGrpcService) }));
    }
}

/// <summary>
///     Housed in an <c>internal</c> container so the offender types are NOT visible via
///     <see cref="System.Reflection.Assembly.GetExportedTypes"/> — otherwise the host
///     fixture would see them during normal scanning and fail to boot the test server.
///     Tests reach them by direct <c>typeof()</c> reference.
/// </summary>
internal static class MisuseFixtures
{
    [WolverineGrpcService]
    public class ConcreteGreeterStub : Greeter.GreeterBase;
}
