namespace Wolverine.Attributes;

public enum MiddlewareScoping
{
    /// <summary>
    /// This middleware always applies
    /// </summary>
    Anywhere,
    
    /// <summary>
    /// This middleware should only be applied when used for message handling
    /// </summary>
    MessageHandlers,
    
    /// <summary>
    /// This middleware should only be applied when running in an HTTP endpoint
    /// </summary>
    HttpEndpoints
}

public abstract class ScopedMiddlewareAttribute : Attribute
{
    public MiddlewareScoping Scoping { get; set; } = MiddlewareScoping.Anywhere;

    public ScopedMiddlewareAttribute(MiddlewareScoping scoping)
    {
        Scoping = scoping;
    }

    protected ScopedMiddlewareAttribute()
    {
    }
}

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called before the actual handler
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineBeforeAttribute : ScopedMiddlewareAttribute
{
    public WolverineBeforeAttribute(MiddlewareScoping scoping) : base(scoping)
    {
    }

    public WolverineBeforeAttribute()
    {
    }
}

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called after the actual handler
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineAfterAttribute : ScopedMiddlewareAttribute
{
    public WolverineAfterAttribute(MiddlewareScoping scoping) : base(scoping)
    {
    }

    public WolverineAfterAttribute()
    {
    }
}

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called after the actual handler in the finally block of
///     a try/finally block around the message handlers
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineFinallyAttribute : ScopedMiddlewareAttribute
{
    public WolverineFinallyAttribute(MiddlewareScoping scoping) : base(scoping)
    {
    }

    public WolverineFinallyAttribute()
    {
    }
}