using GreeterCodeFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Messages;
using GreeterProtoFirstGrpc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-3235: Wolverine.Grpc projects its discovered proto-first + code-first unary services into the core
// IGrpcEndpointManifest abstraction so diagnostic consumers (CritterWatch) can read the endpoint -> message-type
// mapping without referencing WolverineFx.Grpc.
[Collection(GrpcSerialTestsCollection.Name)]
public class grpc_endpoint_manifest_3235
{
    [Fact]
    public async Task projects_proto_first_and_code_first_unary_endpoints()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Proto-first Greeter stub lives in the GreeterProtoFirstGrpc.Server assembly.
                opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                // Pull in the code-first contract assembly so it is discovered too.
                opts.Discovery.IncludeAssembly(typeof(IGreeterCodeFirstService).Assembly);
            })
            .ConfigureServices(services => services.AddWolverineGrpc())
            .StartAsync();

        var manifest = host.Services.GetRequiredService<IGrpcEndpointManifest>();

        // Reading Endpoints self-triggers discovery (no MapWolverineGrpcServices required).
        var endpoints = manifest.Endpoints;

        endpoints.ShouldNotBeEmpty();

        // v1 surfaces only the unary, bus-forwarding flavors.
        endpoints.ShouldAllBe(e =>
            e.Mode == GrpcServiceDiscoveryMode.ProtoFirst || e.Mode == GrpcServiceDiscoveryMode.CodeFirst);

        // Proto-first: Greeter.SayHello forwards HelloRequest to the bus. ServiceName is the proto service's simple
        // name (Wolverine's GrpcServiceChain.ProtoServiceName), not the package-qualified "greet.Greeter".
        var sayHello = endpoints.Single(e =>
            e.Mode == GrpcServiceDiscoveryMode.ProtoFirst && e.MethodName == "SayHello");
        sayHello.ServiceName.ShouldBe("Greeter");
        sayHello.RequestType.ShouldBe(typeof(HelloRequest));
        sayHello.ResponseType.ShouldBe(typeof(HelloReply));
        sayHello.HandlerType.ShouldBe(typeof(GreeterGrpcService));

        // The other proto-first unary RPCs are present...
        endpoints.ShouldContain(e =>
            e.Mode == GrpcServiceDiscoveryMode.ProtoFirst && e.MethodName == "SayGoodbye");

        // ...but the server-streaming RPC (StreamGreetings, in both the proto and the code-first contract) is excluded.
        endpoints.ShouldNotContain(e => e.MethodName == "StreamGreetings");

        // Code-first: IGreeterCodeFirstService.Greet forwards GreetRequest to the bus.
        var greet = endpoints.Single(e =>
            e.Mode == GrpcServiceDiscoveryMode.CodeFirst && e.MethodName == "Greet");
        greet.RequestType.ShouldBe(typeof(GreetRequest));
        greet.ResponseType.ShouldBe(typeof(GreetReply));
        greet.ServiceName.ShouldBe("GreeterCodeFirstService");
        greet.HandlerType.ShouldBe(typeof(IGreeterCodeFirstService));
    }

    [Fact]
    public async Task manifest_is_not_registered_without_grpc()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        host.Services.GetService<IGrpcEndpointManifest>().ShouldBeNull();
    }
}
