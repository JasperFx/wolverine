namespace Wolverine.Configuration;

/// <summary>
/// Concrete registration of a custom <c>Result&lt;T&gt;</c>-style type with Wolverine's railway-
/// programming support. Backs the <c>opts.UseResultType&lt;TResult&gt;(...)</c> /
/// <c>opts.UseResultType(typeof(Result&lt;&gt;), ...)</c> entry points. See GH-2221.
///
/// Two construction shapes:
///   - Typed closed: <c>new ResultTypeRegistration&lt;Result&lt;OrderPlaced&gt;&gt;(stopWhen,
///     unwrapWith, errorsFrom)</c>. <see cref="ResultType" /> is the closed type; predicates are
///     invoked with strongly-typed arguments.
///   - Open generic: <c>ResultTypeRegistration.ForOpenGeneric(typeof(Result&lt;&gt;), ...)</c>.
///     <see cref="ResultType" /> is the open-generic definition; predicates operate on
///     <see cref="object" /> at the call boundary, and <see cref="GetUnwrappedType" /> resolves the
///     payload type per closed form via <c>candidate.GetGenericArguments()[unwrappedArgumentIndex]</c>.
/// </summary>
public sealed class ResultTypeRegistration<TResult> : IResultTypeRegistration
{
    private readonly Func<TResult, bool> _stopWhen;
    private readonly Func<TResult, object?> _unwrapWith;
    private readonly Func<TResult, IEnumerable<string>> _errorsFrom;

    public ResultTypeRegistration(
        Func<TResult, bool> stopWhen,
        Func<TResult, object?> unwrapWith,
        Func<TResult, IEnumerable<string>> errorsFrom)
    {
        _stopWhen = stopWhen ?? throw new ArgumentNullException(nameof(stopWhen));
        _unwrapWith = unwrapWith ?? throw new ArgumentNullException(nameof(unwrapWith));
        _errorsFrom = errorsFrom ?? throw new ArgumentNullException(nameof(errorsFrom));
        ResultType = typeof(TResult);
    }

    public Type ResultType { get; }
    public bool IsOpenGeneric => false;

    public bool Matches(Type candidate) => candidate == ResultType;

    public bool ShouldStop(object result) => _stopWhen((TResult)result);

    public object? Unwrap(object result) => _unwrapWith((TResult)result);

    public IEnumerable<string> Errors(object result) => _errorsFrom((TResult)result);

    public Type? GetUnwrappedType(Type closedResultType)
    {
        // For closed registrations the payload type is whatever the user said it would be — we
        // can't infer it from CLR generics because the registered type isn't necessarily an open
        // generic. Caller-side unwrap will still work because Unwrap() returns the value boxed as
        // object and the call site dispatches on the AWAITED type, not the inferred one.
        if (!closedResultType.IsGenericType) return null;
        return closedResultType.GetGenericArguments()[0];
    }
}

/// <summary>
/// Factory + open-generic variant of <see cref="ResultTypeRegistration{TResult}" />. Use
/// <see cref="ForOpenGeneric" /> when registering an open-generic Result definition such as
/// <c>typeof(FluentResults.Result&lt;&gt;)</c> — the same registration then covers every closed
/// form (<c>Result&lt;OrderPlaced&gt;</c>, <c>Result&lt;int&gt;</c>, …) without a per-T entry.
/// </summary>
public static class ResultTypeRegistration
{
    /// <summary>
    /// Build a registration keyed on an open-generic Result definition. The predicates operate on
    /// <see cref="object" /> at the boundary so they can be invoked uniformly across every closed
    /// form. <paramref name="unwrappedArgumentIndex" /> is the generic-argument slot that holds the
    /// success payload type (almost always 0 — <c>Result&lt;T&gt;</c>, <c>OneOf&lt;T, …&gt;</c>);
    /// override it for unusual layouts.
    /// </summary>
    public static IResultTypeRegistration ForOpenGeneric(
        Type openGenericType,
        Func<object, bool> stopWhen,
        Func<object, object?> unwrapWith,
        Func<object, IEnumerable<string>> errorsFrom,
        int unwrappedArgumentIndex = 0)
    {
        if (openGenericType is null) throw new ArgumentNullException(nameof(openGenericType));
        if (!openGenericType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Expected an open-generic type definition (e.g. typeof(Result<>)) but got '{openGenericType.FullName}'. " +
                "For closed types use the typed constructor `new ResultTypeRegistration<TResult>(...)` instead.",
                nameof(openGenericType));
        }

        return new OpenGenericResultTypeRegistration(openGenericType, stopWhen, unwrapWith, errorsFrom,
            unwrappedArgumentIndex);
    }

    /// <summary>
    /// Build a registration for a non-generic Result type (no payload — failure carries only error
    /// messages, success carries no value). Used by <c>opts.UseResultType&lt;Result&gt;(...)</c>
    /// without a payload argument.
    /// </summary>
    public static IResultTypeRegistration ForNonGeneric<TResult>(
        Func<TResult, bool> stopWhen,
        Func<TResult, IEnumerable<string>> errorsFrom)
        => new ResultTypeRegistration<TResult>(stopWhen, _ => null, errorsFrom);

    private sealed class OpenGenericResultTypeRegistration : IResultTypeRegistration
    {
        private readonly Func<object, bool> _stopWhen;
        private readonly Func<object, object?> _unwrapWith;
        private readonly Func<object, IEnumerable<string>> _errorsFrom;
        private readonly int _unwrappedArgumentIndex;

        internal OpenGenericResultTypeRegistration(Type openGenericType,
            Func<object, bool> stopWhen,
            Func<object, object?> unwrapWith,
            Func<object, IEnumerable<string>> errorsFrom,
            int unwrappedArgumentIndex)
        {
            ResultType = openGenericType;
            _stopWhen = stopWhen ?? throw new ArgumentNullException(nameof(stopWhen));
            _unwrapWith = unwrapWith ?? throw new ArgumentNullException(nameof(unwrapWith));
            _errorsFrom = errorsFrom ?? throw new ArgumentNullException(nameof(errorsFrom));
            _unwrappedArgumentIndex = unwrappedArgumentIndex;
        }

        public Type ResultType { get; }
        public bool IsOpenGeneric => true;

        public bool Matches(Type candidate)
        {
            if (candidate is null) return false;
            if (!candidate.IsGenericType) return false;
            return candidate.GetGenericTypeDefinition() == ResultType;
        }

        public bool ShouldStop(object result) => _stopWhen(result);
        public object? Unwrap(object result) => _unwrapWith(result);
        public IEnumerable<string> Errors(object result) => _errorsFrom(result);

        public Type? GetUnwrappedType(Type closedResultType)
        {
            if (!Matches(closedResultType)) return null;
            var args = closedResultType.GetGenericArguments();
            return args.Length > _unwrappedArgumentIndex ? args[_unwrappedArgumentIndex] : null;
        }
    }
}
