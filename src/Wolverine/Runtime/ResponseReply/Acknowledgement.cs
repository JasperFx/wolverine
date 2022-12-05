using System;

namespace Wolverine.Runtime.ResponseReply;

/// <summary>
///     Successful receipt of an outgoing message
/// </summary>
public class Acknowledgement
{
    /// <summary>
    ///     The message id of the original request
    /// </summary>
    public Guid RequestId { get; set; }

    /// <summary>
    ///     The time at which the acknowledgement was sent according to the sender
    ///     of the acknowledgement
    /// </summary>
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