using System.Reflection;

namespace Wolverine.Logging;

/// <summary>
/// Configuration item for a member on the message type that should be audited
/// in instrumentation
/// </summary>
/// <param name="Member"></param>
/// <param name="OpenTelemetryName"></param>
public record AuditedMember(MemberInfo Member, string MemberName, string OpenTelemetryName);