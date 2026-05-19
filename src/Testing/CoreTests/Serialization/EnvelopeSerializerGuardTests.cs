using System.IO;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

[Collection("EnvelopeSerializerLimits")]
public class EnvelopeSerializerGuardTests
{
    [Fact]
    public void default_limits_have_expected_values()
    {
        var defaults = EnvelopeReaderLimits.Default;
        defaults.MaxBatchSize.ShouldBe(1_000);
        defaults.MaxDataSize.ShouldBe(4 * 1024 * 1024);
        defaults.MaxHeaderCount.ShouldBe(128);
    }

    [Fact]
    public void envelope_serializer_limits_default_to_envelope_reader_limits_default()
    {
        EnvelopeSerializer.Limits.ShouldBe(EnvelopeReaderLimits.Default);
    }

    [Fact]
    public void read_many_rejects_oversize_batch_count()
    {
        var buffer = WriteInt32(EnvelopeReaderLimits.Default.MaxBatchSize + 1);
        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.ReadMany(buffer))
            .Message.ShouldContain("batch size");
    }

    [Fact]
    public void read_many_rejects_negative_batch_count()
    {
        var buffer = WriteInt32(-1);
        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.ReadMany(buffer))
            .Message.ShouldContain("range");
    }

    [Fact]
    public void read_many_rejects_batch_count_impossible_for_buffer()
    {
        // 500 envelopes claimed (well under MaxBatchSize) but the buffer contains
        // only the 4-byte count itself — impossible to fit 500 envelopes.
        var buffer = WriteInt32(500);
        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.ReadMany(buffer))
            .Message.ShouldContain("impossible");
    }

    [Fact]
    public void deserialize_rejects_oversize_data_byte_count()
    {
        var buffer = WriteSingleEnvelopePrefix(
            headerCount: 0,
            byteCount: EnvelopeReaderLimits.Default.MaxDataSize + 1);
        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.Deserialize(buffer))
            .Message.ShouldContain("data size");
    }

    [Fact]
    public void deserialize_rejects_byte_count_beyond_remaining_buffer()
    {
        // byteCount is under the data-size cap but the buffer carries no trailing data bytes.
        var buffer = WriteSingleEnvelopePrefix(headerCount: 0, byteCount: 1024);
        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.Deserialize(buffer))
            .Message.ShouldContain("remain in the buffer");
    }

    [Fact]
    public void deserialize_rejects_oversize_header_count()
    {
        var buffer = WriteSingleEnvelopePrefix(
            headerCount: EnvelopeReaderLimits.Default.MaxHeaderCount + 1,
            byteCount: 0);
        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.Deserialize(buffer))
            .Message.ShouldContain("header count");
    }

    private static byte[] WriteInt32(int value)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(value);
        bw.Flush();
        return ms.ToArray();
    }

    private static byte[] WriteSingleEnvelopePrefix(int headerCount, int byteCount)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(DateTime.UtcNow.ToBinary()); // SentAt (int64)
        bw.Write(headerCount);
        bw.Write(byteCount);
        bw.Flush();
        return ms.ToArray();
    }
}
