using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;

namespace Wolverine.Pubsub;

public class PubsubServerOptions {
	public PubsubTopicOptions Topic { get; set; } = new();
	public PubsubSubscriptionOptions Subscription { get; set; } = new();
}

public class PubsubTopicOptions {
	public TopicName Name { get; set; } = default!;
}

public class PubsubSubscriptionOptions {
	public SubscriptionName Name { get; set; } = default!;
	public CreateSubscriptionOptions Options = new();
}

public class PubsubClientOptions {
	public long MaxOutstandingMessages = 1000;
	public long MaxOutstandingByteCount = 100 * 1024 * 1024;
	public PubsubRetryPolicy RetryPolicy = new();
}

public class CreateSubscriptionOptions {
	public int AckDeadlineSeconds = 10;
	public DeadLetterPolicy? DeadLetterPolicy = null;
	public bool EnableExactlyOnceDelivery = false;
	public bool EnableMessageOrdering = false;
	public ExpirationPolicy? ExpirationPolicy = null;
	public string? Filter = null;
	public Duration MessageRetentionDuration = Duration.FromTimeSpan(TimeSpan.FromDays(7));
	public bool RetainAckedMessages = false;
	public RetryPolicy RetryPolicy = new RetryPolicy {
		MinimumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(10)),
		MaximumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(600))
	};
}

public class PubsubRetryPolicy {
	public int MaxRetryCount = 5;
	public int RetryDelay = 1000;
}
