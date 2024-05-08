namespace Wolverine.Attributes;

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called before the actual handler
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineBeforeAttribute : Attribute;

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called after the actual handler
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineAfterAttribute : Attribute;

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called after the actual handler in the finally block of
///     a try/finally block around the message handlers
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineFinallyAttribute : Attribute;