using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Logging;

/// <summary>
///     Configuration item for a member on the message type that should be audited
///     in instrumentation
/// </summary>
/// <param name="Member"></param>
/// <param name="OpenTelemetryName"></param>
public record AuditedMember(MemberInfo Member, string MemberName, string OpenTelemetryName)
{
    public static IEnumerable<AuditedMember> GetAllFromType(Type type)
    {
        foreach (var property in type.GetProperties())
        {
            if (property.TryGetAttribute<AuditAttribute>(out var ratt))
            {
                yield return Create(property, ratt.Heading);
            }
        }

        foreach (var field in type.GetFields())
        {
            if (field.TryGetAttribute<AuditAttribute>(out var ratt))
            {
                yield return Create(field, ratt.Heading);
            }
        }
    }
    public static AuditedMember Create(MemberInfo member, string? heading)
        => new(member, heading ?? member.Name,
            member.Name.SplitPascalCase().Replace(" ", ".").ToLowerInvariant());
}