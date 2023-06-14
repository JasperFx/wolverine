using Wolverine.Util;

namespace Wolverine.Attributes;

/// <summary>
///     Used to override Wolverine's default behavior for identifying a message type.
///     Useful for integrating with other services without having to share a DTO
///     type
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MessageIdentityAttribute : Attribute
{
    /// <summary>
    ///     Explicitly map this message type to a hard coded message type identity
    /// </summary>
    /// <param name="alias"></param>
    public MessageIdentityAttribute(string? alias)
    {
        Alias = alias;
    }

    /// <summary>
    ///     Explicitly forward the message type identity for this message type to another type
    ///     This may be helpful for NServiceBus, MassTransit, or other external tooling interoperability
    /// </summary>
    /// <param name="forwardToType"></param>
    public MessageIdentityAttribute(Type forwardToType) : this(forwardToType.ToMessageTypeName())
    {
    }

    public string? Alias { get; }

    public int Version { get; set; }

    public string? GetName()
    {
        return Version == 0 ? Alias : $"{Alias}.V{Version}";
    }
}