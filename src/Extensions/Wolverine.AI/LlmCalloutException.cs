namespace Wolverine.AI;

/// <summary>
/// Thrown when a callout reached the model but the answer could not be turned into the requested
/// response type. Treated as terminal by the default error policy — a retry sends the identical prompt
/// and gets the identical unusable answer — so the callout is dead lettered with the raw model output
/// on the exception for triage.
/// </summary>
public class LlmCalloutException : Exception
{
    public LlmCalloutException(string message) : base(message)
    {
    }

    public LlmCalloutException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// The raw text the model returned, when there was any. Null when the failure was in resolving the
    /// response type rather than in parsing an answer.
    /// </summary>
    public string? RawResponse { get; init; }
}

/// <summary>
/// Thrown by the budget middleware for a callout that would exceed the configured spend limits. Dead
/// lettered rather than retried: the same callout will exceed the same budget on every attempt, and
/// retrying it is exactly the runaway spend the budget exists to stop.
/// </summary>
public class LlmBudgetExceededException : Exception
{
    public LlmBudgetExceededException(string message) : base(message)
    {
    }
}
