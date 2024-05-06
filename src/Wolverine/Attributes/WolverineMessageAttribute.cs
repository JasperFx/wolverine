namespace Wolverine.Attributes;

/// <summary>
///     Marker interface to denote that a type is a Wolverine
///     message. This is strictly for diagnostic purposes!
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class WolverineMessageAttribute : Attribute;