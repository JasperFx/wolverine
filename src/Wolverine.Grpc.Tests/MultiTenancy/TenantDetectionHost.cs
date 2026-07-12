using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;

namespace Wolverine.Grpc.Tests.MultiTenancy;

/// <summary>
///     Boots a standalone in-process ASP.NET Core + Wolverine gRPC host for a single tenant
///     detection configuration. Each GH-3368 test permutation needs its own
///     <see cref="WolverineGrpcOptions"/> (tenant detection is baked into generated code at
///     bootstrap), so this is a disposable per-configuration host rather than a shared fixture.
/// </summary>
public sealed class TenantDetectionHost : IAsyncDisposable
{
    private WebApplication? _app;

    public GrpcChannel? Channel { get; private set; }

    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("Host has not been started yet.");

    public static async Task<TenantDetectionHost> StartAsync(
        Action<WolverineGrpcOptions>? configureGrpc = null,
        Action<WebApplication>? configureApp = null)
    {
        var host = new TenantDetectionHost();

        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(TenantDetectionHost).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(TenantEchoHandler));
            // For the hand-written-flavor test: PropagationEchoGrpcService delegates to the bus,
            // whose handler must be discoverable in this host too.
            opts.Discovery.IncludeType(typeof(ServerPropagation.PropagationEchoHandler));
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc(configureGrpc);

        host._app = builder.Build();

        // e.g. a fake-authentication middleware stamping HttpContext.User for claim detection —
        // must run before routing/gRPC dispatch.
        configureApp?.Invoke(host._app);

        host._app.UseRouting();
        host._app.MapWolverineGrpcServices();

        await host._app.StartAsync();

        var handler = host._app.GetTestServer().CreateHandler();
        host.Channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        return host;
    }

    public TService CreateClient<TService>() where TService : class
        => Channel!.CreateGrpcService<TService>();

    public async ValueTask DisposeAsync()
    {
        Channel?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
