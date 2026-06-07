using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// Optional contract an exception can implement to control the exception type and message that are
/// written to dead-letter storage, independently of the exception's runtime type. This lets an
/// <see cref="IDeadLetterInterceptor"/> redact the message while still reporting the original
/// exception type, so operators keep the ability to filter and triage dead letters by type.
/// </summary>
public interface IDeadLetterExceptionInfo
{
    /// <summary>The exception type name to persist (for example, the original thrown type).</summary>
    string ExceptionType { get; }

    /// <summary>The exception message to persist.</summary>
    string ExceptionMessage { get; }
}

/// <summary>
/// Resolves the exception type and message strings that stores persist to dead-letter storage.
/// </summary>
public static class DeadLetterExceptionExtensions
{
    /// <summary>
    /// The exception type name to persist. Honors <see cref="IDeadLetterExceptionInfo"/> when the
    /// exception implements it; otherwise the runtime type's code-friendly full name.
    /// </summary>
    public static string? DeadLetterExceptionType(this Exception? exception) =>
        exception is IDeadLetterExceptionInfo info ? info.ExceptionType : exception?.GetType().FullNameInCode();

    /// <summary>
    /// The exception message to persist. Honors <see cref="IDeadLetterExceptionInfo"/> when the
    /// exception implements it; otherwise <see cref="Exception.Message"/>.
    /// </summary>
    public static string? DeadLetterExceptionMessage(this Exception? exception) =>
        exception is IDeadLetterExceptionInfo info ? info.ExceptionMessage : exception?.Message;
}
