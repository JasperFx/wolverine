using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Xunit;

namespace Wolverine.Http.Grpc.Tests;

/// <summary>
/// Shared fixture that boots an in-process ASP.NET Core + Wolverine gRPC host
/// using the ASP.NET Core TestHost (no real network port).
/// </summary>
public class GrpcTestFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // Route the host through the in-memory test server
        builder.WebHost.UseTestServer();

        // Wolverine — discover handlers and register services
        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(GrpcTestFixture).Assembly;
        });

        // Code-first gRPC (user's responsibility — we don't call this in AddWolverineGrpc)
        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();
        builder.Services.AddSingleton<PingTracker>();

        _app = builder.Build();

        _app.UseRouting();
        // Explicit registration — MapWolverineGrpcServices() discovery is tested separately
        _app.MapGrpcService<PingGrpcService>();
        _app.MapGrpcService<PingStreamGrpcService>();
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
    /// Creates a typed code-first gRPC client for <typeparamref name="TService"/>.
    /// </summary>
    public TService CreateClient<TService>() where TService : class
        => Channel!.CreateGrpcService<TService>();
}
