namespace Wolverine.Runtime.Serialization;

/// <summary>
/// Thrown by <see cref="EnvelopeSerializer"/> when an inbound payload declares
/// a wire-format length that exceeds the configured limits or is otherwise
/// inconsistent with the buffer. Transports map this to a deterministic
/// 4xx-style failure rather than allowing an unbounded allocation to crash
/// the host.
/// </summary>
public sealed class InvalidEnvelopeException : Exception
{
    public InvalidEnvelopeException(string message) : base(message) { }
    public InvalidEnvelopeException(string message, Exception inner) : base(message, inner) { }
}
