using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Xunit;

namespace Wolverine.Grpc.Tests.ServerPropagation;

/// <summary>
///     A standalone in-process host (not the shared <see cref="Client.WolverineGrpcClientFixture"/>)
///     with <see cref="WolverineGrpcOptions.PropagateEnvelopeHeaders"/> turned off, so tests can
///     prove the interceptor honors the off-switch without affecting the shared fixture's
///     default-on host used by every other propagation test.
/// </summary>
public class DisabledPropagationFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(DisabledPropagationFixture).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(PropagationEchoHandler));
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc(o => o.PropagateEnvelopeHeaders = false);

        _app = builder.Build();
        _app.UseRouting();
        _app.MapGrpcService<PropagationEchoGrpcService>();

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

    public IPropagationEchoService CreateClient() => Channel!.CreateGrpcService<IPropagationEchoService>();
}
