namespace Wolverine.Attributes;

/// <summary>
/// Marks a class as being a Wolverine handler class. Can also be used
/// on a method within a Wolverine handler class if the user wants to
/// vary from the built in naming conventions
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class WolverineHandlerAttribute : Attribute
{
    
}