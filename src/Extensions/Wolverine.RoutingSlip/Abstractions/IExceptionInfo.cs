namespace Wolverine.RoutingSlip.Abstractions;

/// <summary>
///     An exception information that is serializable
/// </summary>
public interface IExceptionInfo
{
    /// <summary>
    ///     The type name of the exception
    /// </summary>
    string ExceptionType { get; }

    /// <summary>
    ///     The inner exception if present
    /// </summary>
    IExceptionInfo? InnerException { get; }

    /// <summary>
    ///     The stack trace of the exception site
    /// </summary>
    string StackTrace { get; }

    /// <summary>
    ///     The exception message
    /// </summary>
    string Message { get; }

    /// <summary>
    ///     The exception source
    /// </summary>
    string Source { get; }
}