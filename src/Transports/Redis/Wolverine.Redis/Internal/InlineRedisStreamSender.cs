using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Redis.Internal;

public class InlineRedisStreamSender : ISender
{
    private readonly RedisTransport _transport;
    private readonly RedisStreamEndpoint _endpoint;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger _logger;
    
    public InlineRedisStreamSender(RedisTransport transport, RedisStreamEndpoint endpoint, IWolverineRuntime runtime)
    {
        _transport = transport;
        _endpoint = endpoint;
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<InlineRedisStreamSender>();
    }

    public Uri Destination => _endpoint.Uri;
    
    public bool SupportsNativeScheduledSend => false;
    
    public async Task<bool> PingAsync()
    {
        try
        {
            var database = _transport.GetDatabase();
            // Simple ping to check if Redis is available
            await database.PingAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ping Redis server");
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        try
        {
            var database = _transport.GetDatabase();
            
            // Use envelope mapper to create Redis stream fields
            _endpoint.EnvelopeMapper ??= _endpoint.BuildMapper(_runtime);

            var fields = new List<NameValueEntry>();
            _endpoint.EnvelopeMapper!.MapEnvelopeToOutgoing(envelope, fields);

            // Send to Redis Stream using XADD
            var messageId = await database.StreamAddAsync(_endpoint.StreamKey, fields.ToArray());
            
            _logger.LogDebug("Sent message {MessageId} to Redis stream {StreamKey} with ID {StreamMessageId}", 
                envelope.Id, _endpoint.StreamKey, messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message {MessageId} to Redis stream {StreamKey}", 
                envelope.Id, _endpoint.StreamKey);
            throw;
        }
    }


}
