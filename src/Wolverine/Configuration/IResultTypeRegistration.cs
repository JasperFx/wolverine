namespace Wolverine.Configuration;

/// <summary>
/// Non-generic façade over <see cref="ResultTypeRegistration{TResult}" /> so the codegen seams,
/// caller-side InvokeAsync&lt;T&gt; unwrap, and the registry can operate uniformly across both
/// closed and open-generic registrations. See GH-2221.
/// </summary>
public interface IResultTypeRegistration
{
    /// <summary>The registered Result type — either a closed type (e.g. <c>FluentResults.Result&lt;OrderPlaced&gt;</c>)
    /// or an open-generic type definition (e.g. <c>typeof(FluentResults.Result&lt;&gt;)</c>).</summary>
    Type ResultType { get; }

    /// <summary>True when <see cref="ResultType" /> is an open-generic definition that must be closed
    /// before matching against a concrete handler return type.</summary>
    bool IsOpenGeneric { get; }

    /// <summary>Does this registration cover <paramref name="candidate" />? Handles exact match (closed
    /// against closed) and generic-definition match (open against any of its closures).</summary>
    bool Matches(Type candidate);

    /// <summary>True when the runtime <paramref name="result" /> value represents a failure / stop
    /// continuation. Invokes the user's <c>StopWhen</c> predicate.</summary>
    bool ShouldStop(object result);

    /// <summary>Extract the success payload from a successful result. Returns null when the
    /// registration is for a non-generic Result type that carries no payload.</summary>
    object? Unwrap(object result);

    /// <summary>Materialize the error messages from a failed result. Empty enumerable when the
    /// result is a success (callers should gate on <see cref="ShouldStop" /> first).</summary>
    IEnumerable<string> Errors(object result);

    /// <summary>Given a closed Result type, return the success payload's CLR type — i.e. the
    /// generic argument for an open-generic registration like <c>Result&lt;T&gt;</c>. Returns null
    /// for non-generic Result types that have no payload.</summary>
    Type? GetUnwrappedType(Type closedResultType);
}
