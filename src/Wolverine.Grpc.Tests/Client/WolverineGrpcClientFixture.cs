using GreeterProtoFirstGrpc.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Server;
using Xunit;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     Hosts the server-side <see cref="WebApplication"/> for the typed-client tests. The
///     fixture only cares about exposing a <see cref="HttpMessageHandler"/> that round-trips
///     back into the Wolverine + gRPC stack — every test owns its own isolated client-side
///     <see cref="IServiceCollection"/> so per-client options (address, exception maps, propagation
///     toggle) do not bleed between tests.
/// </summary>
public class WolverineGrpcClientFixture : IAsyncLifetime
{
    private WebApplication? _app;

    /// <summary>
    ///     A <see cref="HttpMessageHandler"/> that routes outbound HTTP/2 requests back into the
    ///     in-process <see cref="WebApplication"/> without a real network port. Pass to
    ///     <see cref="Grpc.Net.Client.GrpcChannelOptions.HttpHandler"/> via
    ///     <c>WolverineGrpcClientBuilder.ConfigureChannel(...)</c>.
    /// </summary>
    public HttpMessageHandler ServerHandler { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;
            opts.Discovery.IncludeAssembly(typeof(WolverineGrpcClientFixture).Assembly);
            opts.Discovery.IncludeAssembly(typeof(GreeterGrpcService).Assembly);
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddGrpc();
        builder.Services.AddWolverineGrpc();
        builder.Services.AddSingleton<PingTracker>();

        _app = builder.Build();
        _app.UseRouting();

        // Server endpoints used by the client tests — MapWolverineGrpcServices auto-discovers
        // every type ending in "GrpcService" from the scanned assemblies, so PingGrpcService,
        // HeaderEchoGrpcService, and FaultingGrpcService are all mapped here. Don't double-map.
        _app.MapWolverineGrpcServices();

        await _app.StartAsync();

        ServerHandler = _app.GetTestServer().CreateHandler();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

[CollectionDefinition("grpc-client")]
public class GrpcClientCollection : ICollectionFixture<WolverineGrpcClientFixture>
{
}
