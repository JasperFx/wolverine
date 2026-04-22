using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.Grpc.Internals;

internal class GrpcSender : ISender, IDisposable
{
    private readonly ILogger<GrpcSender> _logger;
    private readonly GrpcChannel _channel;
    private readonly WolverineTransport.WolverineTransportClient _client;

    public GrpcSender(Uri destination, string host, int port, ILogger<GrpcSender> logger)
    {
        Destination = destination;
        _logger = logger;
        _channel = GrpcChannel.ForAddress($"http://{host}:{port}");
        _client = new WolverineTransport.WolverineTransportClient(_channel);
    }

    public bool SupportsNativeScheduledSend => false;

    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            var result = await _client.PingAsync(new PingRequest());
            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ping to {Destination} failed", Destination);
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var data = EnvelopeSerializer.Serialize(envelope);
        var message = new WolverineMessage { Data = ByteString.CopyFrom(data) };
        await _client.SendAsync(message);
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
