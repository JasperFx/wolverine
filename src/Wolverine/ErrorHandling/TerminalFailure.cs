namespace Wolverine.ErrorHandling;

/// <summary>
/// GH-5556. Marks an exception type as a terminal failure: retrying cannot help, so Wolverine skips the retry
/// policies and goes straight to what comes after them -- dead-lettering the message, publishing its
/// <see cref="Fault{T}" />, and compensating a saga. The interface form of <see cref="TerminalFailureAttribute" />.
/// </summary>
/// <remarks>SPEC STUB (GH-5556): specified by Bobcat specifications first; Wolverine does not act on it yet.</remarks>
public interface ITerminalFailure;

/// <summary>
/// GH-5556. Marks an exception type as a terminal failure: retrying cannot help, so Wolverine skips the retry
/// policies and goes straight to what comes after them. The attribute form of <see cref="ITerminalFailure" />.
/// </summary>
/// <remarks>SPEC STUB (GH-5556): specified by Bobcat specifications first; Wolverine does not act on it yet.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class TerminalFailureAttribute : Attribute;
