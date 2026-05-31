using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Grpc.Internals;

internal class GrpcListener : IListener
{
    private readonly IReceiver _receiver;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<GrpcListener> _logger;
    private WebApplication? _app;

    public GrpcListener(Uri address, int port, IReceiver receiver, IWolverineRuntime runtime, ILogger<GrpcListener> logger)
    {
        Address = address;
        Port = port;
        _receiver = receiver;
        _runtime = runtime;
        _logger = logger;
    }

    public int Port { get; }

    public IHandlerPipeline? Pipeline => null;

    public Uri Address { get; }

    internal async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ListenAnyIP(Port, o => o.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_receiver);
        builder.Services.AddSingleton<IListener>(this);
        // GH-2967: the inline-request/reply Call handler executes against the main application's runtime.
        builder.Services.AddSingleton(_runtime);

        _app = builder.Build();
        _app.MapGrpcService<WolverineGrpcTransportService>();

        await _app.StartAsync();
        _logger.LogInformation("gRPC transport listener started on port {Port}", Port);
    }

    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public async ValueTask StopAsync()
    {
        if (_app != null)
        {
            _logger.LogInformation("Stopping gRPC transport listener on port {Port}", Port);
            await _app.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
