using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Grpc.Tests.GrpcClientStreaming.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcClientStreaming;

/// <summary>
///     Boots an in-process gRPC host to exercise proto-first client-streaming code-gen
///     end-to-end. Isolated from other fixtures so client-streaming assertions don't drift
///     when unary/server-streaming/bidi tests evolve.
/// </summary>
public class ClientStreamingFixture : IAsyncLifetime
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
            opts.ApplicationAssembly = typeof(ClientStreamingFixture).Assembly;
        });

        builder.Services.AddGrpc();
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

    public CollectTest.CollectTestClient CreateClient()
        => new(Channel!);
}
