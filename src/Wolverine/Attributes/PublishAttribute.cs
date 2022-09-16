using System;

namespace Wolverine.Attributes;

/// <summary>
///     Marks a concrete type as an event or message that is published by the current system
///     This is only for the purpose of capability diagnostics
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class PublishAttribute : Attribute
{
}
