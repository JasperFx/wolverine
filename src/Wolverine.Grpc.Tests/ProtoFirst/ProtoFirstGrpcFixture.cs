using GreeterProtoFirstGrpc.Server;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wolverine.Grpc.Tests.ProtoFirst;

/// <summary>
///     Boots an in-process ASP.NET Core + Wolverine host for the proto-first Greeter service.
///     No handwritten bridge: the Wolverine-generated wrapper around the abstract
///     <c>GreeterGrpcService</c> stub (defined in the <c>GreeterProtoFirstGrpc.Server</c>
///     sample) is mapped by <c>MapWolverineGrpcServices</c>.
/// </summary>
public class ProtoFirstGrpcFixture : IAsyncLifetime
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
            opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
        });

        // Proto-first requires the stock ASP.NET Core gRPC host, not the code-first one.
        builder.Services.AddGrpc();
        builder.Services.AddWolverineGrpc();

        _app = builder.Build();

        _app.UseRouting();

        // Discovery path under test: MapWolverineGrpcServices() must find the abstract
        // [WolverineGrpcService]-annotated GreeterGrpcService, codegen a concrete subclass,
        // and map it via MapGrpcService<T>().
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
}
