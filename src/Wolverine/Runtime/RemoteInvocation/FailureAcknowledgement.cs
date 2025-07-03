using System.Diagnostics;
using System.Text;

namespace Wolverine.Runtime.RemoteInvocation;

public class FailureAcknowledgement : ISerializable, INotToBeRouted
{
    public Guid RequestId { get; init; }
    public string Message { get; init; } = null!;

    public byte[] Write()
    {
        var guid = RequestId.ToByteArray();
        var message = Encoding.UTF8.GetBytes(Message ?? string.Empty);
        return guid.Concat(message).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var requestId = new Guid(bytes[..16]);
        var message = Encoding.UTF8.GetString(bytes[16..]);

        return new FailureAcknowledgement
        {
            Message = message,
            RequestId = requestId
        };
    }

    protected bool Equals(FailureAcknowledgement other)
    {
        return Equals(RequestId, other.RequestId) && string.Equals(Message, other.Message);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((FailureAcknowledgement)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (RequestId.GetHashCode() * 397) ^
                   Message.GetHashCode();
        }
    }

    public override string ToString()
    {
        return $"Failure acknowledgement for {RequestId} / '{Message}'";
    }
}