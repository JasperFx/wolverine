using System.IO;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Serialization;
using Xunit;

namespace CoreTests.Serialization;

/// <summary>
/// GH-5146. gRPC and Pub/Sub hand the wire bytes over as memory (<c>ByteString.Memory</c>), so
/// <see cref="EnvelopeSerializer" /> reads straight off a <see cref="ReadOnlyMemory{T}" /> instead of the
/// listener copying it into a <c>byte[]</c> first. These pin that the memory overloads read exactly what the
/// <c>byte[]</c> ones do, including from a slice that does not start at offset zero, and that the envelope's
/// body is its own copy rather than a view over memory the caller may reuse.
/// </summary>
[Collection("EnvelopeSerializerLimits")]
public class envelope_serializer_reads_from_memory_5146
{
    private static Envelope outgoing(string text) => new()
    {
        Id = Guid.NewGuid(),
        MessageType = "Some.Message",
        CorrelationId = Guid.NewGuid().ToString(),
        TenantId = "tenant-one",
        ContentType = "application/json",
        Destination = new Uri("grpc://localhost:5000"),
        Data = System.Text.Encoding.UTF8.GetBytes(text)
    };

    // Puts the bytes in the middle of a bigger array, so a reader that ignored the offset would fail
    private static ReadOnlyMemory<byte> sliceOf(byte[] bytes)
    {
        var padded = new byte[bytes.Length + 20];
        bytes.CopyTo(padded, 7);
        return new ReadOnlyMemory<byte>(padded, 7, bytes.Length);
    }

    [Fact]
    public void deserialize_from_a_memory_slice_matches_the_array_overload()
    {
        var original = outgoing("hello");
        var bytes = EnvelopeSerializer.Serialize(original);

        var fromMemory = EnvelopeSerializer.Deserialize(sliceOf(bytes));
        var fromArray = EnvelopeSerializer.Deserialize(bytes);

        fromMemory.Id.ShouldBe(original.Id);
        fromMemory.MessageType.ShouldBe(fromArray.MessageType);
        fromMemory.CorrelationId.ShouldBe(original.CorrelationId);
        fromMemory.TenantId.ShouldBe("tenant-one");
        fromMemory.Destination.ShouldBe(original.Destination);
        fromMemory.SentAt.ShouldBe(fromArray.SentAt);
        fromMemory.Data.ShouldBe(original.Data);
    }

    [Fact]
    public void read_many_from_a_memory_slice_matches_the_array_overload()
    {
        var originals = new[] { outgoing("one"), outgoing("two"), outgoing("three") };
        var bytes = EnvelopeSerializer.Serialize(originals);

        var read = EnvelopeSerializer.ReadMany(sliceOf(bytes));

        read.Select(x => x.Id).ShouldBe(originals.Select(x => x.Id));
        read.Select(x => System.Text.Encoding.UTF8.GetString(x.Data!)).ShouldBe(["one", "two", "three"]);
    }

    [Fact]
    public void read_envelope_data_from_a_memory_slice()
    {
        var original = outgoing("data only");
        var bytes = EnvelopeSerializer.Serialize(original);

        var envelope = new Envelope();
        EnvelopeSerializer.ReadEnvelopeData(envelope, sliceOf(bytes));

        envelope.Id.ShouldBe(original.Id);
        envelope.Data.ShouldBe(original.Data);
    }

    [Fact]
    public void the_body_does_not_alias_the_callers_memory()
    {
        var bytes = EnvelopeSerializer.Serialize(outgoing("keep me"));
        var buffer = bytes.ToArray();

        var envelope = EnvelopeSerializer.Deserialize(new ReadOnlyMemory<byte>(buffer));

        // The transport is free to reuse its buffer once the read returns
        Array.Clear(buffer);

        System.Text.Encoding.UTF8.GetString(envelope.Data!).ShouldBe("keep me");
    }

    [Fact]
    public void read_many_from_memory_keeps_the_batch_size_guard()
    {
        // 500 envelopes claimed in a buffer that holds only the count itself
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream)) writer.Write(500);

        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.ReadMany(sliceOf(stream.ToArray())))
            .Message.ShouldContain("impossible");
    }

    [Fact]
    public void a_truncated_memory_slice_is_refused_rather_than_read_past()
    {
        var bytes = EnvelopeSerializer.Serialize(outgoing("this body gets cut off"));

        // The slice stops short of the body, though the array behind it carries on
        var padded = new byte[bytes.Length + 50];
        bytes.CopyTo(padded, 0);
        var truncated = new ReadOnlyMemory<byte>(padded, 0, bytes.Length - 5);

        Should.Throw<InvalidEnvelopeException>(() => EnvelopeSerializer.Deserialize(truncated));
    }
}
