using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Xunit;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     A standalone in-process host (not the shared <see cref="Client.WolverineGrpcClientFixture"/>)
///     that wires the saga handlers used by these tests and maps their gRPC services:
///     <see cref="CountingSaga"/> (header-identified, reproduces the GH-3385 gap) and
///     <see cref="ReservationSaga"/> (message-identified, the HTTP-parity happy path). Uses
///     Wolverine's default in-memory saga persistence, so no database is required.
/// </summary>
public class SagaOverGrpcFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    /// <summary>
    ///     The server host's service provider — used by tests to resolve
    ///     <c>InMemorySagaPersistor</c> and assert saga persistence directly, the in-memory
    ///     equivalent of the HTTP saga test's Marten <c>LoadAsync</c>.
    /// </summary>
    public IServiceProvider Services => _app!.Services;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(SagaOverGrpcFixture).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(CountingSaga));
            opts.Discovery.IncludeType(typeof(ReservationSaga));
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapGrpcService<CountingSagaGrpcService>();
        _app.MapGrpcService<ReservationSagaGrpcService>();

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

    public ICountingSagaService CreateClient() => Channel!.CreateGrpcService<ICountingSagaService>();

    public IReservationSagaService CreateReservationClient() => Channel!.CreateGrpcService<IReservationSagaService>();
}
