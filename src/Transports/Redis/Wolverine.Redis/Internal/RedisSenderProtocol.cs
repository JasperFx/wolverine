using StackExchange.Redis;
using Wolverine.Runtime.Serialization;
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
        var database = _transport.GetDatabase(database: _endpoint.DatabaseId);

        try
        {
            var immediateTasks = new List<Task<RedisValue>>();
            var scheduledTasks = new List<Task<bool>>();
            
            foreach (var envelope in batch.Messages)
            {
                if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
                {
                    // Add to scheduled sorted set
                    var scheduledKey = _endpoint.ScheduledMessagesKey;
                    var serializedEnvelope = EnvelopeSerializer.Serialize(envelope);
                    var score = envelope.ScheduledTime!.Value.ToUnixTimeMilliseconds();
                    scheduledTasks.Add(database.SortedSetAddAsync(scheduledKey, serializedEnvelope, score));
                }
                else
                {
                    // Send immediately to stream
                    var list = new List<NameValueEntry>();
                    _endpoint.EnvelopeMapper!.MapEnvelopeToOutgoing(envelope, list);
                    immediateTasks.Add(database.StreamAddAsync(_endpoint.StreamKey, list.ToArray()));
                }
            }

            await Task.WhenAll(immediateTasks.Cast<Task>().Concat(scheduledTasks.Cast<Task>()));
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
