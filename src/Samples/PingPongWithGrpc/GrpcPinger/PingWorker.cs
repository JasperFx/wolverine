namespace GrpcPinger;

using Grpc.Net.Client;
using GrpcPingPongContracts;
using ProtoBuf.Grpc.Client;

public sealed class PingWorker : BackgroundService
{
    private readonly ILogger<PingWorker> _logger;
    private readonly IConfiguration _config;

    public PingWorker(ILogger<PingWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingNumber = 1;
        var pongerUrl = _config.GetValue<string>("Ponger:Url") ?? "http://localhost:5200";

        // Create a gRPC channel to the Ponger service
        using var channel = GrpcChannel.ForAddress(pongerUrl);
        // Create a code-first gRPC client using protobuf-net.Grpc
        var ponger = channel.CreateGrpcService<IPongerService>();
        
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
            var request = new PingMessage { Number = pingNumber, Message = "Hi!" };
            _logger.LogInformation("Sending Ping #{Number}", pingNumber);
            await ponger.SendPingAsync(request);
            pingNumber++;
        }
    }
}
