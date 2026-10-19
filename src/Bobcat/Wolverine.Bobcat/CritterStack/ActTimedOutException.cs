namespace Bobcat.CritterStack;

/// <summary>
/// bobcat#673. The act ran out of time — its own code, or the harness waiting on it (a tracked session that timed out
/// before everything the act caused had settled), threw a <see cref="TimeoutException" />. Recorded on the act's own
/// When step, which reads <b>error</b>, so a reader starts at the act rather than at the Then steps that found
/// nothing to judge. The timeout the act raised is the <see cref="Exception.InnerException" />, and stays on
/// <see cref="CritterStackActOutcome.Error" /> exactly as it was.
/// </summary>
/// <remarks>
/// Never thrown: the scenario carries on to its Then steps, as it does after any act that failed. A
/// <see cref="TimeoutException" />, so code that caught the timeout before still catches this one.
/// </remarks>
public sealed class ActTimedOutException : TimeoutException
{
    public ActTimedOutException(TimeoutException timeout, string? hint = null)
        : base(describe(timeout, hint), timeout)
    {
        Hint = hint;
    }

    /// <summary>What a derived scenario added about why its act may have run out of time, or null.</summary>
    public string? Hint { get; }

    private static string describe(TimeoutException timeout, string? hint)
        => $"The act timed out: {timeout.Message}" + (string.IsNullOrWhiteSpace(hint) ? "" : $" {hint}");
}
