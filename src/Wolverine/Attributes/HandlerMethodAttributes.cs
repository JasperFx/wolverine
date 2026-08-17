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
    HttpEndpoints,

    /// <summary>
    /// This middleware should only be applied when running in a proto-first gRPC service chain
    /// </summary>
    Grpc
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
///     Marks a method on middleware types or handler types as a method that should be called
///     <b>after the transactional commit</b>, rather than merely after the handler.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="WolverineAfterAttribute" /> and the <c>After()</c> convention run after the handler but
///         <b>before</b> the commit: the commit is itself a postprocessor contributed by the persistence
///         provider, and <c>After</c> methods are inserted at the FRONT of the postprocessor list. That is a
///         reasonable default for "after the handler", but it means an <c>After</c> method observing a write
///         is observing one that is not durable yet and may still fail.
///     </para>
///     <para>
///         A method marked with this attribute — or conventionally named <c>AfterCommit</c> /
///         <c>AfterCommitAsync</c> — is appended to <see cref="IChain.PostCommitPostprocessors" />, which is
///         concatenated after every postprocessor at frame assembly time. The position is therefore structural
///         and does not depend on the order policies happened to run in.
///     </para>
///     <para>
///         These methods do NOT run when the commit throws: the frames are concatenated without a
///         try/finally, so the exception unwinds straight past them. That is deliberate — the reason to want
///         "after the commit" is almost always that the side effect must not happen for a write that did not
///         land.
///     </para>
///     <para>
///         Note this runs after the outbox flush as well as the commit, so cascading messages returned from an
///         after-commit method are sent through the normal end-of-pipeline flush rather than the just-committed
///         transaction's outbox. If a message must be atomic with the write, cascade it from the handler.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineAfterCommitAttribute : ScopedMiddlewareAttribute
{
    public WolverineAfterCommitAttribute(MiddlewareScoping scoping) : base(scoping)
    {
    }

    public WolverineAfterCommitAttribute()
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

/// <summary>
///     Marks a method on middleware types or handler types as a method
///     that should be called in a catch block when an exception of the specified
///     type is thrown during handler execution. The first parameter of the method
///     must be the exception type to catch.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class WolverineOnExceptionAttribute : ScopedMiddlewareAttribute
{
    public WolverineOnExceptionAttribute(MiddlewareScoping scoping) : base(scoping)
    {
    }

    public WolverineOnExceptionAttribute()
    {
    }
}