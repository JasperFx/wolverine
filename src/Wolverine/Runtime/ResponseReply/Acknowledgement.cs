using System;

namespace Wolverine.Runtime.ResponseReply;

public class Acknowledgement
{
    public Guid RequestId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    
    protected bool Equals(Acknowledgement other)
    {
        return Equals(RequestId, other.RequestId);
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

        return Equals((Acknowledgement)obj);
    }

    public override int GetHashCode()
    {
        return RequestId.GetHashCode();
    }
}
