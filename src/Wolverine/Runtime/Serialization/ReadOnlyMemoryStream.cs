namespace Wolverine.Runtime.Serialization;

/// <summary>
///     GH-5146. A read-only, seekable <see cref="Stream" /> over a <see cref="ReadOnlyMemory{T}" />, so
///     <see cref="EnvelopeSerializer" /> can read a wire payload that a transport hands over as memory
///     (gRPC and Pub/Sub <c>ByteString.Memory</c>, a slice of a pooled buffer) without first copying it
///     into a <c>byte[]</c>. The BCL has the same type, but it is internal to System.Private.CoreLib.
/// </summary>
internal sealed class ReadOnlyMemoryStream : Stream
{
    private readonly ReadOnlyMemory<byte> _content;
    private int _position;

    public ReadOnlyMemoryStream(ReadOnlyMemory<byte> content)
    {
        _content = content;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _content.Length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, (long)int.MaxValue);
            _position = (int)value;
        }
    }

    public override int ReadByte()
    {
        var span = _content.Span;
        return _position < span.Length ? span[_position++] : -1;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var remaining = _content.Length - _position;
        if (remaining <= 0 || buffer.Length == 0) return 0;

        var count = Math.Min(remaining, buffer.Length);
        _content.Span.Slice(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _content.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        Position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
