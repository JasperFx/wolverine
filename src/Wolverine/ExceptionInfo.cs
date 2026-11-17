namespace Wolverine;

/// <summary>
/// Wire-stable, serializable snapshot of an exception. Carries the type name,
/// message, optional stack trace, and a recursive view of the inner-exception chain.
/// </summary>
public record ExceptionInfo(
    string Type,
    string Message,
    string? StackTrace,
    IReadOnlyList<ExceptionInfo> InnerExceptions)
{
    private const string BaseExceptionTypeName = "System.Exception";

    /// <summary>
    /// The full names of the exception's type and its base types, most derived first, stopping before
    /// <see cref="System.Exception"/>. For <c>OutOfStockException : InventoryException</c> this is
    /// <c>[OutOfStockException, InventoryException]</c> (as full names). It lets a receiver in another
    /// process ask <see cref="Is{TException}"/> about a base type without loading the thrown type.
    /// <para>
    /// Empty when the snapshot came from a sender that predates this property; <see cref="Is(string)"/>
    /// then falls back to matching <see cref="Type"/> alone. Type names carry no payload, so neither
    /// redaction flag on <see cref="From(Exception, bool, bool)"/> affects it.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> TypeHierarchy { get; init; } = [];

    /// <summary>
    /// Is the captured exception of the type named by <paramref name="fullTypeName"/>, or derived from it?
    /// Checks <see cref="Type"/>, then <see cref="TypeHierarchy"/>. <c>"System.Exception"</c> always matches.
    /// </summary>
    public bool Is(string fullTypeName)
    {
        ArgumentNullException.ThrowIfNull(fullTypeName);

        if (fullTypeName == BaseExceptionTypeName) return true;
        if (Type == fullTypeName) return true;

        foreach (var name in TypeHierarchy)
        {
            if (name == fullTypeName) return true;
        }

        return false;
    }

    /// <summary>
    /// Is the captured exception a <typeparamref name="TException"/>, or derived from it? Matches by full
    /// type name, so it works for a failure that was thrown in another process.
    /// </summary>
    public bool Is<TException>() where TException : Exception
        => Is(typeof(TException).FullName ?? typeof(TException).Name);

    /// <summary>
    /// Build a wire-stable snapshot from a live <see cref="Exception"/>, recursing
    /// through <see cref="Exception.InnerException"/> and
    /// <see cref="AggregateException.InnerExceptions"/>.
    /// <para>
    /// When <paramref name="includeMessage"/> is <c>false</c>, every captured
    /// <see cref="ExceptionInfo.Message"/> in the resulting chain is set to
    /// <see cref="string.Empty"/>. When <paramref name="includeStackTrace"/> is
    /// <c>false</c>, every captured <see cref="ExceptionInfo.StackTrace"/> is
    /// set to <c>null</c>. The <see cref="ExceptionInfo.Type"/> field is always
    /// preserved. Both flags default to <c>true</c>; the no-arg call site is
    /// equivalent to <c>From(exception, true, true)</c>.
    /// </para>
    /// </summary>
    public static ExceptionInfo From(
        Exception exception,
        bool includeMessage = true,
        bool includeStackTrace = true)
        => From(exception, includeMessage, includeStackTrace, depth: 0);

    private const int MaxDepth = 32;
    private const string TruncatedTypeMarker = "__truncated__";
    private static readonly string TruncatedMessage = $"[Inner exception chain truncated at depth {MaxDepth}]";

    private static ExceptionInfo From(
        Exception exception,
        bool includeMessage,
        bool includeStackTrace,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (depth >= MaxDepth)
        {
            return new ExceptionInfo(
                Type: TruncatedTypeMarker,
                Message: TruncatedMessage,
                StackTrace: null,
                InnerExceptions: Array.Empty<ExceptionInfo>());
        }

        var inners = exception switch
        {
            AggregateException agg => agg.InnerExceptions
                .Select(e => From(e, includeMessage, includeStackTrace, depth + 1))
                .ToArray(),
            _ when exception.InnerException is { } inner =>
                new[] { From(inner, includeMessage, includeStackTrace, depth + 1) },
            _ => Array.Empty<ExceptionInfo>(),
        };

        return new ExceptionInfo(
            Type: typeName(exception.GetType()),
            Message: includeMessage ? exception.Message : string.Empty,
            StackTrace: includeStackTrace ? exception.StackTrace : null,
            InnerExceptions: inners)
        {
            TypeHierarchy = typeHierarchy(exception.GetType())
        };
    }

    private static string typeName(Type type) => type.FullName ?? type.Name;

    private static string[] typeHierarchy(Type exceptionType)
    {
        var names = new List<string>();
        for (var type = exceptionType; type != null && type != typeof(Exception); type = type.BaseType)
        {
            names.Add(typeName(type));
        }

        return names.ToArray();
    }
}
