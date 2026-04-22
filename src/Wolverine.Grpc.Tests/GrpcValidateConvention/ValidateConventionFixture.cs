using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping;
using Wolverine.Grpc.Tests.GrpcValidateConvention.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcValidateConvention;

/// <summary>
///     Boots an in-process gRPC host to exercise the <c>Validate → Status?</c> short-circuit
///     convention end-to-end. Isolated from other fixtures so validate-specific assertions
///     don't drift when the middleware-scoping tests evolve.
/// </summary>
public class ValidateConventionFixture : IAsyncLifetime
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
            opts.ApplicationAssembly = typeof(ValidateConventionFixture).Assembly;
        });

        builder.Services.AddSingleton(Sink);
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

    public ValidatorGreeterTest.ValidatorGreeterTestClient CreateClient()
        => new(Channel!);
}
