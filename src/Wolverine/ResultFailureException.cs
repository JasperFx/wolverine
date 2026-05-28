namespace Wolverine;

/// <summary>
/// Thrown to the caller of <c>InvokeAsync&lt;T&gt;</c> when the receiving handler returned a
/// registered Result type in its failure state and the caller awaited the inner payload <c>T</c>
/// (not the wrapper <c>Result&lt;T&gt;</c>). Callers that want to inspect the failure without
/// catching this exception can switch to awaiting the wrapper directly:
/// <c>await bus.InvokeAsync&lt;Result&lt;T&gt;&gt;(...)</c>. See GH-2221.
/// </summary>
public sealed class ResultFailureException : Exception
{
    public ResultFailureException(IReadOnlyList<string> errors)
        : base(FormatMessage(errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }

    private static string FormatMessage(IReadOnlyList<string> errors)
    {
        if (errors == null || errors.Count == 0)
        {
            return "Handler returned a failed Result with no error messages";
        }

        return errors.Count == 1
            ? errors[0]
            : "Handler returned a failed Result: " + string.Join("; ", errors);
    }
}
