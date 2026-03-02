namespace Wolverine.Attributes;

/// <summary>
/// When applied to a generic interface or class, tells Wolverine that this type wraps a message
/// and that the first generic type argument is the actual message type for handler discovery.
/// This allows Wolverine to correctly route messages to handlers that accept wrapper types
/// such as <c>ConsumeContext&lt;T&gt;</c> from MassTransit shims.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class)]
public class WolverineMessageWrapperAttribute : Attribute;
