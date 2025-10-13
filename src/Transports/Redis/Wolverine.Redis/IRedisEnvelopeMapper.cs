using StackExchange.Redis;
using Wolverine.Transports;

namespace Wolverine.Redis;

/// <summary>
/// Envelope mapper interface for Redis Streams transport
/// </summary>
public interface IRedisEnvelopeMapper : IEnvelopeMapper<StreamEntry, List<NameValueEntry>>
{

}