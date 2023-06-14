namespace Wolverine.Attributes;

/// <summary>
///     Marks a member on a message type to be audited in message activity logging, Open Telemetry activity tags,
///     and in performance metrics
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class AuditAttribute : Attribute
{
    public AuditAttribute()
    {
    }

    /// <summary>
    ///     Override the audit heading instead of using the member name
    /// </summary>
    /// <param name="heading"></param>
    public AuditAttribute(string? heading)
    {
        Heading = heading;
    }

    /// <summary>
    ///     Optional
    /// </summary>
    public string? Heading { get; set; }
}