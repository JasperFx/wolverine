using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Xunit;

namespace Wolverine.Grpc.Tests.ExplicitRegistration;

public class ExplicitRegistrationFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("Fixture has not been initialized yet.");

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(ExplicitRegistrationFixture).Assembly;
        });

        builder.Services.AddCodeFirstGrpc();
        // Twice on purpose: a repeat AddWolverineGrpc re-runs the callback and must not add a second chain.
        builder.Services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IExplicitlyRegisteredService>());
        builder.Services.AddWolverineGrpc(grpc => grpc.IncludeCodeFirstContract<IExplicitlyRegisteredService>());

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

    public async ValueTask DisposeAsync()
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
