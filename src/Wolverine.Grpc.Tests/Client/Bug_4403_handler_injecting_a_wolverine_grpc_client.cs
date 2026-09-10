using GreeterProtoFirstGrpc.Messages;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PingPongWithGrpc.Messages;
using Shouldly;
using Wolverine.Grpc.Client;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
/// GH-4403. The report hosts a gRPC server and client together behind ASP.NET Core, but none of that is
/// involved: this is a plain host with no ASP.NET, no gRPC server and no AddWolverineGrpc(), and a handler that
/// merely takes a Wolverine gRPC client as a parameter. AddWolverineGrpcClient registers the client through an
/// opaque lambda factory -- Microsoft's AddGrpcClient for proto-first clients, Wolverine's own for code-first --
/// which codegen can only reach through service location, and Wolverine 6 defaults to
/// ServiceLocationPolicy.NotAllowed. Both of the first two tests threw InvalidServiceLocationException until
/// AddWolverineGrpcClient began opting its client into service location itself.
/// </summary>
public class Bug_4403_handler_injecting_a_wolverine_grpc_client
{
    private static async Task<IHost> hostWith(Type handlerType, Action<IServiceCollection> registerClient,
        Action<WolverineOptions>? configure = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(Bug_4403_handler_injecting_a_wolverine_grpc_client).Assembly;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);
                configure?.Invoke(opts);
            })
            .ConfigureServices(registerClient)
            .StartAsync();
    }

    private static void protoFirstClient(IServiceCollection services)
        => services.AddWolverineGrpcClient<Greeter.GreeterClient>(o => o.Address = new Uri("http://localhost:1"));

    private static void codeFirstClient(IServiceCollection services)
        => services.AddWolverineGrpcClient<IPingService>(o => o.Address = new Uri("http://localhost:1"));

    [Fact]
    public async Task a_handler_can_take_a_proto_first_client()
    {
        using var host = await hostWith(typeof(UsesProtoFirstClientHandler), protoFirstClient);

        await host.InvokeMessageAndWaitAsync(new UseProtoFirstClient());
    }

    [Fact]
    public async Task a_handler_can_take_a_code_first_client()
    {
        using var host = await hostWith(typeof(UsesCodeFirstClientHandler), codeFirstClient);

        await host.InvokeMessageAndWaitAsync(new UseCodeFirstClient());
    }

    // The reporter's workaround, which existing applications will still carry. Opting in explicitly as well
    // must keep working alongside the automatic registration rather than colliding with it.
    [Fact]
    public async Task an_explicit_opt_in_still_works_alongside_the_automatic_one()
    {
        using var host = await hostWith(typeof(UsesProtoFirstClientHandler), protoFirstClient,
            opts => opts.CodeGeneration.AlwaysUseServiceLocationFor<Greeter.GreeterClient>());

        await host.InvokeMessageAndWaitAsync(new UseProtoFirstClient());
    }
}

public record UseProtoFirstClient;

public record UseCodeFirstClient;

public static class UsesProtoFirstClientHandler
{
    // Never calls the client -- there is no server. Taking it as a parameter is the whole reproduction.
    public static void Handle(UseProtoFirstClient message, Greeter.GreeterClient client)
    {
        client.ShouldNotBeNull();
    }
}

public static class UsesCodeFirstClientHandler
{
    public static void Handle(UseCodeFirstClient message, IPingService client)
    {
        client.ShouldNotBeNull();
    }
}
