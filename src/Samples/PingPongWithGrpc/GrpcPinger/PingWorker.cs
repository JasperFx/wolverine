using Grpc.Net.Client;
using PingPongContracts;
using ProtoBuf.Grpc.Client;

namespace GrpcPinger;

/// <summary>
/// Background worker that periodically sends a Ping to the Ponger service over gRPC
/// and logs the Pong response.
/// </summary>
public sealed class PingWorker : BackgroundService
{
    private readonly ILogger<PingWorker> _logger;
    private readonly IConfiguration _configuration;

    public PingWorker(ILogger<PingWorker> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pongerUrl = _configuration.GetValue<string>("Ponger:Url") ?? "http://localhost:5200";

        _logger.LogInformation("GrpcPinger connecting to Ponger at {Url}", pongerUrl);

        // Create a gRPC channel to the Ponger service
        using var channel = GrpcChannel.ForAddress(pongerUrl);

        // Create a code-first gRPC client using protobuf-net.Grpc
        var ponger = channel.CreateGrpcService<IPongerService>();

        var pingNumber = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            var request = new PingMessage { Number = pingNumber, Message = $"Hello from Pinger #{pingNumber}" };
            _logger.LogInformation("Sending Ping #{Number}", pingNumber);

            try
            {
                var pong = await ponger.SendPingAsync(request);
                _logger.LogInformation("Received Pong #{Number}: {Message}", pong.Number, pong.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending Ping #{Number}", pingNumber);
            }

            pingNumber++;
        }
    }
}
