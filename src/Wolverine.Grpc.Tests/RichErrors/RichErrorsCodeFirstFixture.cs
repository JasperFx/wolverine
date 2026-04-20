using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using ProtoBuf.Grpc.Server;
using Wolverine.FluentValidation;
using Wolverine.FluentValidation.Grpc;
using Xunit;

namespace Wolverine.Grpc.Tests.RichErrors;

/// <summary>
///     Boots an in-process ASP.NET Core + Wolverine host exercising the full rich-error-details
///     pipeline: FluentValidation middleware, the gRPC rich-details interceptor layer, and the
///     FluentValidation →&#160;<c>BadRequest</c> adapter. A <see cref="GreetCommand"/> with an empty
///     <c>Name</c> should surface as <see cref="Google.Rpc.Code.InvalidArgument"/> with a packed
///     <c>BadRequest</c> in the <c>grpc-status-details-bin</c> trailer.
/// </summary>
public class RichErrorsCodeFirstFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public GrpcChannel? Channel { get; private set; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(RichErrorsCodeFirstFixture).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(RichErrorsGreeterHandler));

            opts.UseFluentValidation();
            opts.UseGrpcRichErrorDetails();
            opts.UseFluentValidationGrpcErrorDetails();
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        _app = builder.Build();
        _app.UseRouting();
        _app.MapGrpcService<RichErrorsGreeterGrpcService>();

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

    public IRichErrorsGreeterService CreateClient() => Channel!.CreateGrpcService<IRichErrorsGreeterService>();
}
