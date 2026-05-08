namespace Wolverine;

/// <summary>
/// A strongly-typed event published by Wolverine when a handler permanently fails
/// to process a message of type <typeparamref name="T"/> — i.e. all retries are
/// exhausted and the envelope has been moved to the dead-letter queue
/// (or discarded, when discarded faults are opted in).
/// </summary>
public record Fault<T>(
    T Message,
    ExceptionInfo Exception,
    int Attempts,
    DateTimeOffset FailedAt,
    string? CorrelationId,
    Guid ConversationId,
    string? TenantId,
    string? Source,
    IReadOnlyDictionary<string, string?> Headers
) where T : class;
