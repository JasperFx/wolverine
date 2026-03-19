using Wolverine.RoutingSlip.Abstractions;

namespace Wolverine.RoutingSlip;

/// <summary>
/// Serializable representation of an exception for routing slip fault events.
/// </summary>
public sealed class ExceptionInfo : IExceptionInfo
{
    private ExceptionInfo(string exceptionType, IExceptionInfo? innerException, string stackTrace, string message, string source)
    {
        ExceptionType = exceptionType;
        InnerException = innerException;
        StackTrace = stackTrace;
        Message = message;
        Source = source;
    }

    /// <summary>
    /// Creates an <see cref="ExceptionInfo" /> tree from an <see cref="Exception" />.
    /// </summary>
    /// <param name="exception">The exception to map.</param>
    /// <returns>A serializable exception payload including inner exceptions.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception" /> is <c>null</c>.</exception>
    public static ExceptionInfo From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new ExceptionInfo(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.InnerException is null ? null : From(exception.InnerException),
            exception.StackTrace ?? string.Empty,
            exception.Message,
            exception.Source ?? string.Empty
        );
    }

    /// <summary>
    /// Fully qualified type name of the exception.
    /// </summary>
    public string ExceptionType { get; }
    /// <summary>
    /// Mapped inner exception, if present.
    /// </summary>
    public IExceptionInfo? InnerException { get; }
    /// <summary>
    /// Captured stack trace text. Empty when unavailable.
    /// </summary>
    public string StackTrace { get; }
    /// <summary>
    /// Exception message text.
    /// </summary>
    public string Message { get; }
    /// <summary>
    /// Exception source value. Empty when unavailable.
    /// </summary>
    public string Source { get; }
}
