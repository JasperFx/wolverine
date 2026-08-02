namespace Wolverine.ComplianceTests.ErrorHandling;

public class ErrorCausingMessage
{
    /// <summary>
    /// Identifies one specific message under test. The transport compliance suites share broker
    /// queues across test methods, and a redelivery still in flight when one test's tracked session
    /// completes will show up in the *next* test's session. Assertions have to scope to this id
    /// rather than to the message type, or a straggler becomes "the ending activity."
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public Dictionary<int, Exception> Errors { get; set; } = new();
    public bool WasProcessed { get; set; }
    public int LastAttempt { get; set; }
}