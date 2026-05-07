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
    {
        ArgumentNullException.ThrowIfNull(exception);

        var inners = exception switch
        {
            AggregateException agg => agg.InnerExceptions
                .Select(e => From(e, includeMessage, includeStackTrace))
                .ToArray(),
            _ when exception.InnerException is { } inner =>
                new[] { From(inner, includeMessage, includeStackTrace) },
            _ => Array.Empty<ExceptionInfo>(),
        };

        return new ExceptionInfo(
            Type: exception.GetType().FullName ?? exception.GetType().Name,
            Message: includeMessage ? exception.Message : string.Empty,
            StackTrace: includeStackTrace ? exception.StackTrace : null,
            InnerExceptions: inners);
    }
}
