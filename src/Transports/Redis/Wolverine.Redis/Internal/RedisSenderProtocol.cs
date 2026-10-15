using StackExchange.Redis;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Redis.Internal;

public class RedisSenderProtocol : ISenderProtocolWithNativeScheduling, IDisposable
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

        // One command per envelope, each answered on its own, so a failure is reported per envelope
        var envelopes = new List<Envelope>(batch.Messages.Count);
        var tasks = new List<Task>(batch.Messages.Count);

        try
        {
            foreach (var envelope in batch.Messages)
            {
                if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
                {
                    // Add to scheduled sorted set
                    var scheduledKey = _endpoint.ScheduledMessagesKey;
                    var serializedEnvelope = EnvelopeSerializer.Serialize(envelope);
                    var score = envelope.ScheduledTime!.Value.ToUnixTimeMilliseconds();
                    tasks.Add(database.SortedSetAddAsync(scheduledKey, serializedEnvelope, score));
                    envelopes.Add(envelope);
                }
                else
                {
                    // Send immediately to stream
                    var list = new List<NameValueEntry>();
                    _endpoint.EnvelopeMapper!.MapEnvelopeToOutgoing(envelope, list);
                    tasks.Add(database.StreamAddAsync(_endpoint.StreamKey, list.ToArray()));
                    envelopes.Add(envelope);
                }
            }

            await Task.WhenAll(tasks);
            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception ex)
        {
            // Report what Redis took as sent and only the rest as failed. Failing the whole batch would send the
            // accepted ones again on retry. Not rethrown: the callback has been told, and a BatchedSender that
            // caught it would fail the whole batch a second time
            var succeeded = envelopes.Where((_, i) => tasks[i].IsCompletedSuccessfully).ToList();
            var failed = batch.Messages.Except(succeeded).ToList();

            if (succeeded.Count > 0)
            {
                await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, succeeded));
            }

            await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, failed), ex);
        }
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}
