namespace TestingSupport.ErrorHandling;

public class ErrorCausingMessage
{
    public Dictionary<int, Exception> Errors { get; set; } = new();
    public bool WasProcessed { get; set; }
    public int LastAttempt { get; set; }
}