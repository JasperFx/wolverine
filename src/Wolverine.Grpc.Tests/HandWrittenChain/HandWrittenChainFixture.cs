using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping;
using Xunit;

namespace Wolverine.Grpc.Tests.HandWrittenChain;

/// <summary>
///     Boots an in-process ASP.NET Core + Wolverine host to exercise the hand-written-chain
///     codegen path end-to-end. <see cref="HandWrittenTestGrpcService"/> is discovered via
///     the <c>GrpcService</c> suffix convention; Wolverine generates a delegation wrapper and
///     maps it via <c>MapWolverineGrpcServices()</c>.
/// </summary>
public class HandWrittenChainFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }
    public MiddlewareInvocationSink Sink { get; } = new();

    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("Fixture has not been initialized yet.");

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(HandWrittenChainFixture).Assembly;
        });

        builder.Services.AddSingleton(Sink);
        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();
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

    public TService CreateClient<TService>() where TService : class
        => Channel!.CreateGrpcService<TService>();
}
