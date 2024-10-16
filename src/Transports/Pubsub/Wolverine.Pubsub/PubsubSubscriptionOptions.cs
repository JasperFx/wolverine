using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;

namespace Wolverine.Pubsub;

public class PubsubSubscriptionOptions {
	public long? MaxOutstandingMessages = null;
	public long? MaxOutstandingByteCount = null;

	public int AckDeadlineSeconds = 10;
	public DeadLetterPolicy? DeadLetterPolicy = null;
	public bool EnableExactlyOnceDelivery = false;
	public bool EnableMessageOrdering = false;
	public ExpirationPolicy? ExpirationPolicy = null;
	public string? Filter = null;
	public Duration? MessageRetentionDuration = null;
	public bool RetainAckedMessages = false;
	public RetryPolicy? RetryPolicy = null;
}
