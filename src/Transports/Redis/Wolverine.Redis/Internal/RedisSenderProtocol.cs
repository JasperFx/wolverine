using StackExchange.Redis;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Redis.Internal;

public class RedisSenderProtocol : ISenderProtocol, IDisposable
{
    private readonly RedisTransport _transport;
    private readonly RedisStreamEndpoint _endpoint;

    public RedisSenderProtocol(RedisTransport transport, RedisStreamEndpoint endpoint)
    {
        _transport = transport;
        _endpoint = endpoint;
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        var database = _transport.GetDatabase();

        try
        {
            var tasks = new List<Task<RedisValue>>(batch.Messages.Count);
            foreach (var envelope in batch.Messages)
            {
                var list = new List<NameValueEntry>();
                _endpoint.EnvelopeMapper!.MapEnvelopeToOutgoing(envelope, list);
                tasks.Add(database.StreamAddAsync(_endpoint.StreamKey, list.ToArray()));
            }

            await Task.WhenAll(tasks);
            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception ex)
        {
            await callback.MarkProcessingFailureAsync(batch, ex);
            throw;
        }
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}
