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
    /// <summary>
    /// Build a wire-stable snapshot from a live <see cref="Exception"/>, recursing
    /// through <see cref="Exception.InnerException"/> and
    /// <see cref="AggregateException.InnerExceptions"/>.
    /// </summary>
    public static ExceptionInfo From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var inners = exception switch
        {
            AggregateException agg => agg.InnerExceptions
                .Select(From)
                .ToArray(),
            _ when exception.InnerException is { } inner => new[] { From(inner) },
            _ => Array.Empty<ExceptionInfo>(),
        };

        return new ExceptionInfo(
            Type: exception.GetType().FullName ?? exception.GetType().Name,
            Message: exception.Message,
            StackTrace: exception.StackTrace,
            InnerExceptions: inners);
    }
}
