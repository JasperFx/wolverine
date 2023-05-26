namespace Wolverine.Attributes;

/// <summary>
/// This attribute helps Wolverine "know" that when interoperating with
/// another messaging tool like NServiceBus or MassTransit that wants
/// to send or receive by interface types, that this concrete 
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class InteropMessageAttribute : Attribute
{
    public Type InteropType { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="interopType">The interopType will be used as the identity for this message type when being sent </param>
    public InteropMessageAttribute(Type interopType)
    {
        InteropType = interopType;
    }
}