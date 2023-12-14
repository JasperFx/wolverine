
using System.Diagnostics;

namespace Wolverine.Runtime.RemoteInvocation;

public class FailureAcknowledgement
{
    public Guid RequestId { get; init; }
    public string Message { get; init; } = null!;

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