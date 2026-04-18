using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Xunit;
using StreamingPonger = PingPongWithGrpcStreaming.Ponger;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     Shared fixture that boots an in-process ASP.NET Core + Wolverine gRPC host over the
///     ASP.NET Core TestHost (no real network port). The services + handlers under test live
///     in the <c>src/Samples/PingPongWithGrpc</c> and <c>src/Samples/PingPongWithGrpcStreaming</c>
///     sample projects — this fixture pulls them in via <see cref="Discovery"/> and maps them.
/// </summary>
public class GrpcTestFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            // ApplicationAssembly is the unary sample. Handlers from the streaming sample and
            // this test assembly (FaultingHandler) are pulled in explicitly.
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;
            opts.Discovery.IncludeAssembly(typeof(StreamingPonger.PingStreamGrpcService).Assembly);
            opts.Discovery.IncludeAssembly(typeof(GrpcTestFixture).Assembly);
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        // Each sample defines its own PingTracker — both must be registered.
        builder.Services.AddSingleton<PingTracker>();
        builder.Services.AddSingleton<StreamingPonger.PingTracker>();

        _app = builder.Build();

        _app.UseRouting();

        // Explicit registration — MapWolverineGrpcServices() discovery is tested separately.
        _app.MapGrpcService<PingGrpcService>();
        _app.MapGrpcService<StreamingPonger.PingStreamGrpcService>();
        _app.MapGrpcService<FaultingGrpcService>();

        await _app.StartAsync();

        var handler = _app.GetTestServer().CreateHandler();

        Channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    public async Task DisposeAsync()
    {
        Channel?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    ///     Creates a typed code-first gRPC client for <typeparamref name="TService"/>.
    /// </summary>
    public TService CreateClient<TService>() where TService : class
        => Channel!.CreateGrpcService<TService>();
}
