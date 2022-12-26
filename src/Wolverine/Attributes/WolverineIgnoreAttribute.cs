namespace Wolverine.Attributes;

/// <summary>
///     Place on a concrete class or method to make Wolverine ignore the class or methods
///     in message handler or http endpoint action discovery
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly)]
public class WolverineIgnoreAttribute : Attribute
{
}