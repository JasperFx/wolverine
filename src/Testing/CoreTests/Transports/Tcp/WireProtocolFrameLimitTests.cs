using System.IO;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports.Tcp;

[Collection("EnvelopeSerializerLimits")]
public class WireProtocolFrameLimitTests
{
    [Fact]
    public async Task receive_async_rejects_oversize_frame_length()
    {
        // Arrange: a stream whose first 4 bytes encode int.MaxValue —
        // well above WireProtocol.MaxFrameSize (32 MiB).
        // ReceiveAsync should catch the InvalidEnvelopeException thrown by the
        // guard, write SerializationFailureBuffer to the stream, and return
        // normally — without ever attempting to allocate int.MaxValue bytes.
        var originalMaxFrameSize = WireProtocol.MaxFrameSize;
        try
        {
            WireProtocol.MaxFrameSize = 32 * 1024 * 1024; // explicit 32 MiB for this test

            using var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(int.MaxValue); // 4-byte hostile length prefix
            writer.Flush();
            ms.Position = 0;

            var receiver = new Protocol.StubReceiverCallback();

            // Act: should NOT throw — the catch block handles InvalidEnvelopeException
            // and writes SerializationFailureBuffer before returning.
            await WireProtocol.ReceiveAsync(null!, NullLogger.Instance, ms, receiver);

            // Assert: the bytes written after the initial 4-byte read are
            // SerializationFailureBuffer.
            var written = ms.ToArray().Skip(sizeof(int)).ToArray();
            written.ShouldBe(WireProtocol.SerializationFailureBuffer);
        }
        finally
        {
            WireProtocol.MaxFrameSize = originalMaxFrameSize;
        }
    }

    [Fact]
    public async Task receive_async_rejects_negative_frame_length()
    {
        // Negative lengths (bit-pattern trick) must also be rejected.
        var originalMaxFrameSize = WireProtocol.MaxFrameSize;
        try
        {
            WireProtocol.MaxFrameSize = 32 * 1024 * 1024;

            using var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            writer.Write(-1); // negative length
            writer.Flush();
            ms.Position = 0;

            var receiver = new Protocol.StubReceiverCallback();

            await WireProtocol.ReceiveAsync(null!, NullLogger.Instance, ms, receiver);

            var written = ms.ToArray().Skip(sizeof(int)).ToArray();
            written.ShouldBe(WireProtocol.SerializationFailureBuffer);
        }
        finally
        {
            WireProtocol.MaxFrameSize = originalMaxFrameSize;
        }
    }

    [Fact]
    public async Task receive_async_accepts_zero_length_frame()
    {
        // A zero-length frame is a valid ping / no-op that ReceiveAsync handles
        // by returning immediately without error.
        using var ms = new MemoryStream();
        var writer = new BinaryWriter(ms);
        writer.Write(0); // zero length
        writer.Flush();
        ms.Position = 0;

        var receiver = new Protocol.StubReceiverCallback();

        // Should return without writing any response bytes.
        await WireProtocol.ReceiveAsync(null!, NullLogger.Instance, ms, receiver);

        // No response written — only the original 4 bytes exist.
        ms.ToArray().Length.ShouldBe(sizeof(int));
    }
}
