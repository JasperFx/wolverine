using GreeterProtoFirstGrpc.Messages;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Messages;
using Shouldly;
using Wolverine.Grpc.Client;
using Xunit;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     Confirms that <see cref="WolverineGrpcClientExtensions.AddWolverineGrpcClient{TClient}"/>
///     routes code-first (<c>[ServiceContract]</c>) and proto-first (generated <c>*Client</c> class)
///     typed clients through the correct substrate — <c>protobuf-net.Grpc</c> code-first factory
///     vs Microsoft's <c>AddGrpcClient&lt;T&gt;()</c>.
/// </summary>
[Collection("grpc-client")]
public class registration_tests
{
    private readonly WolverineGrpcClientFixture _fixture;

    public registration_tests(WolverineGrpcClientFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void code_first_service_contract_is_classified_as_code_first()
    {
        WolverineGrpcClientExtensions.IsCodeFirstContract(typeof(IPingService)).ShouldBeTrue();
    }

    [Fact]
    public void proto_first_generated_client_is_not_classified_as_code_first()
    {
        WolverineGrpcClientExtensions.IsCodeFirstContract(typeof(Greeter.GreeterClient)).ShouldBeFalse();
    }

    [Fact]
    public void code_first_registration_returns_code_first_builder()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = services.AddWolverineGrpcClient<IPingService>(o =>
        {
            o.Address = new Uri("http://localhost");
        });

        builder.Kind.ShouldBe(WolverineGrpcClientKind.CodeFirst);
        builder.HttpClientBuilder.ShouldBeNull();
    }

    [Fact]
    public void proto_first_registration_exposes_http_client_builder()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var builder = services.AddWolverineGrpcClient<Greeter.GreeterClient>(o =>
        {
            o.Address = new Uri("http://localhost");
        });

        builder.Kind.ShouldBe(WolverineGrpcClientKind.ProtoFirst);
        builder.HttpClientBuilder.ShouldNotBeNull();
    }

    [Fact]
    public async Task code_first_client_round_trips_a_unary_call()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWolverineGrpcClient<IPingService>(o =>
        {
            o.Address = new Uri("http://localhost");
        }).ConfigureChannel(c => c.HttpHandler = _fixture.ServerHandler);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IPingService>();

        var reply = await client.Ping(new PingRequest { Message = "hello" });

        reply.Echo.ShouldContain("hello");
    }

    [Fact]
    public async Task proto_first_client_round_trips_a_unary_call()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWolverineGrpcClient<Greeter.GreeterClient>(o =>
        {
            o.Address = new Uri("http://localhost");
        })
        .HttpClientBuilder!
        .ConfigurePrimaryHttpMessageHandler(() => _fixture.ServerHandler);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<Greeter.GreeterClient>();

        var reply = await client.SayHelloAsync(new HelloRequest { Name = "Erik" });

        reply.Message.ShouldBe("Hello, Erik");
    }

    [Fact]
    public void registration_without_address_throws_a_clear_error_at_resolution_time()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWolverineGrpcClient<IPingService>();

        using var provider = services.BuildServiceProvider();

        var ex = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredService<IPingService>());

        ex.Message.ShouldContain("Address");
        ex.Message.ShouldContain(nameof(IPingService));
    }
}
