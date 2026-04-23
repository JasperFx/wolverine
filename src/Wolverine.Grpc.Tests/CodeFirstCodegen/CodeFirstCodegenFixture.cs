using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Xunit;

namespace Wolverine.Grpc.Tests.CodeFirstCodegen;

/// <summary>
///     Boots an in-process ASP.NET Core + Wolverine host for the code-first codegen path.
///     No concrete service class is registered — <c>MapWolverineGrpcServices()</c> discovers
///     <see cref="ICodeFirstTestService"/> (annotated with <c>[WolverineGrpcService]</c>),
///     generates the implementation at startup, and maps it.
/// </summary>
public class CodeFirstCodegenFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("Fixture has not been initialized yet.");

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            // Scan the test assembly so the interface and handlers are both discovered.
            opts.ApplicationAssembly = typeof(CodeFirstCodegenFixture).Assembly;
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();

        // Full codegen discovery path under test: MapWolverineGrpcServices must find
        // ICodeFirstTestService, generate CodeFirstTestServiceGrpcHandler, and map it.
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
