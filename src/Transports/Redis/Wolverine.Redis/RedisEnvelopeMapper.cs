using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Redis;

public class RedisEnvelopeMapper : EnvelopeMapper<StreamEntry, List<NameValueEntry>>, IRedisEnvelopeMapper
{
    /// <summary>
    /// represents the Redis Stream entry ID - used for ACK/requeue operations
    /// </summary>
    public const string RedisEntryIdHeader = "redis-entry-id";

    private const string HeaderPrefix = "wolverine-";

    public RedisEnvelopeMapper(Endpoint endpoint) : base(endpoint)
    {
        MapProperty(x => x.Data!, 
            (e, m) => e.Data = m.Values.FirstOrDefault(x => x.Name == "payload").Value,
            (e, m) => m.Add(new NameValueEntry("payload", e.Data)));
    }

    protected override void writeOutgoingHeader(List<NameValueEntry> outgoing, string key, string value)
    {
        // Do not persist the Redis stream id header as an outgoing field; a new entry will receive its own ID
        if (string.Equals(key, RedisEntryIdHeader, StringComparison.OrdinalIgnoreCase)) 
            return;

        outgoing.Add(new NameValueEntry($"{HeaderPrefix}{key}", value));
    }

    protected override bool tryReadIncomingHeader(StreamEntry incoming, string key, out string? value)
    {
        var target = $"{HeaderPrefix}{key}";
        foreach (var nv in incoming.Values)
        {
            if (nv.Name.Equals(target))
            {
                value = nv.Value.ToString();
                return true;
            }
        }

        value = null;
        return false;
    }

    protected override void writeIncomingHeaders(StreamEntry incoming, Envelope envelope)
    {
        var headers = incoming.Values.Where(k => k.Name.StartsWith(HeaderPrefix));
        foreach (var nv in headers)
        {
            envelope.Headers[nv.Name.ToString()[HeaderPrefix.Length..]] = nv.Value.ToString();
        }

        // Also capture the Redis stream message id so the listener can ACK/DEFER appropriately
        envelope.Headers[RedisEntryIdHeader] = incoming.Id.ToString();
    }

    //public async Task<NameValueEntry[]> ToRedisStreamFields(Envelope envelope)
    //{
    //    var kvps = new List<NameValueEntry>();

    //    MapEnvelopeToOutgoing(envelope, kvps);

    //    return kvps.ToArray();
    //}

    //public Envelope CreateEnvelope(string streamKey, StreamEntry message)
    //{
    //    var envelope = new Envelope
    //    {
    //        Data = message.Values.FirstOrDefault(x => x.Name == "data").Value,
    //        TopicName = streamKey,
    //    };

    //    MapIncomingToEnvelope(envelope, message);

    //    // Ensure the stream id is present on the envelope
    //    envelope.Headers[RedisEntryIdHeader] = message.Id.ToString();

    //    return envelope;
    //}
}
