using System.Text;
using Dekaf.Consumer;
using Dekaf.Serialization;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// Builds Dekaf ConsumeResult values for mapper-level tests, standing in for records that would
/// come off a broker.
/// </summary>
internal static class TestRecords
{
    public static ConsumeResult<string, byte[]> Incoming(
        string topic,
        string? key,
        byte[]? value,
        IEnumerable<KeyValuePair<string, byte[]?>>? headers = null,
        DateTimeOffset? timestamp = null,
        TimestampType timestampType = TimestampType.CreateTime,
        int partition = 0,
        long offset = 0)
    {
        var headerList = headers?
            .Select(pair => pair.Value == null
                ? new Header(pair.Key, ReadOnlyMemory<byte>.Empty, true)
                : new Header(pair.Key, pair.Value))
            .ToList() ?? [];

        return new ConsumeResult<string, byte[]>(
            topic, partition, offset,
            key == null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(key),
            key == null,
            value ?? ReadOnlyMemory<byte>.Empty,
            value == null,
            headerList,
            (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            timestampType,
            null,
            Serializers.String, Serializers.ByteArray,
            false);
    }
}
