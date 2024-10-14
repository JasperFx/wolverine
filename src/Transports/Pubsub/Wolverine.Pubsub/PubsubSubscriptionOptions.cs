namespace Wolverine.Pubsub;

public class PubsubSubscriptionOptions {
    public long? MaxOutstandingMessages = null;
    public long? MaxOutstandingByteCount = null;
    public int MaxRetryCount = 5;
    public int RetryDelay = 2000;
    public string? DeadLetterName = null;
    public int DeadLetterMaxDeliveryAttempts = 5;
}
