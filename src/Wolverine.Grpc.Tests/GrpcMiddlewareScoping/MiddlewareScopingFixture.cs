using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Dedicated host fixture for the M15 <c>MiddlewareScoping.Grpc</c> integration tests.
///     Booted as a per-class fixture (not collection-shared) so each test class can verify
///     middleware-invocation ordering against a fresh <see cref="MiddlewareInvocationSink"/>
///     without inter-test interference.
/// </summary>
/// <remarks>
///     Modeled on <see cref="GrpcTestFixture"/> but isolated from the PingPong/Streaming/Faulting
///     samples so M15 assertions don't drift when those samples evolve. Uses ASP.NET Core's
///     <c>TestHost</c> for an in-process channel — no real network port.
/// </remarks>
public class MiddlewareScopingFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }
    public MiddlewareInvocationSink Sink { get; } = new();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(MiddlewareScopingFixture).Assembly;
        });

        builder.Services.AddSingleton(Sink);
        builder.Services.AddGrpc();
        builder.Services.AddWolverineGrpc();

        _app = builder.Build();

        _app.UseRouting();

        // Trigger Wolverine's proto-first discovery + code-gen and register the generated
        // wrapper. Pre-M15 weaving, this just emits forward-frames; once §7.3 lands the same
        // generated code will additionally carry middleware/postprocessor frames per RPC.
        _app.MapWolverineGrpcServices();

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

    public GreeterMiddlewareTest.GreeterMiddlewareTestClient CreateClient()
        => new(Channel!);
}
