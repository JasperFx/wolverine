using System.Text;

namespace Wolverine.Runtime.RemoteInvocation;

/// <summary>
///     Successful receipt of an outgoing message
/// </summary>
public class Acknowledgement : ISerializable, INotToBeRouted
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

    public byte[] Write()
    {
        var guid = RequestId.ToByteArray();
        var timestamp = Encoding.UTF8.GetBytes(Timestamp.ToString("O"));
        return guid.Concat(timestamp).ToArray();
    }

    public static object Read(byte[] bytes)
    {
        var ack = new Acknowledgement
        {
            RequestId = new Guid(bytes[..16])
        };

        var timestampString = Encoding.UTF8.GetString(bytes[16..]);
        if (DateTimeOffset.TryParse(timestampString, out var timestamp))
        {
            ack.Timestamp = timestamp;
        }

        return ack;
    }

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
        // ReSharper disable once NonReadonlyMemberInGetHashCode
        return RequestId.GetHashCode();
    }
}