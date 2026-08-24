using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;

namespace Wolverine.Pubsub;

public class PubsubServerOptions
{
    public PubsubTopicOptions Topic { get; set; } = new();
    public PubsubSubscriptionOptions Subscription { get; set; } = new();
}

public class PubsubTopicOptions
{
    public CreateTopicOptions Options = new();
    public TopicName Name { get; set; } = default!;

    /// <summary>
    ///     Derives the Pub/Sub <c>OrderingKey</c> for an outgoing message. Configure through
    ///     <see cref="PubsubTopicSubscriberConfiguration.OrderMessagesBy" />. Consulted on every publish, but
    ///     only used when the envelope carries no <see cref="Envelope.GroupId" /> — see GH-4087.
    /// </summary>
    public Func<Envelope, string?> OrderBy = e => null;
}

public class CreateTopicOptions
{
    public Duration MessageRetentionDuration = Duration.FromTimeSpan(TimeSpan.FromMinutes(10));
}

public class PubsubSubscriptionOptions
{
    public CreateSubscriptionOptions Options = new();
    public SubscriptionName Name { get; set; } = default!;
}

public class CreateSubscriptionOptions
{
    public int AckDeadlineSeconds = 10;
    public DeadLetterPolicy? DeadLetterPolicy = null;
    public bool EnableExactlyOnceDelivery = false;
    public bool EnableMessageOrdering = false;
    public ExpirationPolicy? ExpirationPolicy = null;
    public string? Filter = null;
    public Duration MessageRetentionDuration = Duration.FromTimeSpan(TimeSpan.FromDays(7));
    public bool RetainAckedMessages = false;

    public RetryPolicy RetryPolicy = new()
    {
        MinimumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(10)),
        MaximumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(600))
    };
}

public class PubsubClientOptions
{
    public long MaxOutstandingByteCount = 100 * 1024 * 1024;
    public long MaxOutstandingMessages = 1000;

    /// <summary>
    ///     GH-4066. The total budget the Pub/Sub client may spend extending a message's ack deadline while
    ///     Wolverine is still processing it. Wolverine sets this explicitly on every listener rather than
    ///     letting it fall through to the SDK default.
    ///     <para>
    ///     Once this budget is exhausted the client simply <em>stops</em> extending. It does not cancel the
    ///     running callback and it does not raise anything into it — the service just redelivers, so a second
    ///     execution of the same message begins while the first is still in flight. That is materially worse
    ///     than ordinary at-least-once redelivery, because the two executions <em>overlap</em>: optimistic
    ///     concurrency, event stream appends, sagas and group-ordered processing all assume a message is not
    ///     running concurrently with itself.
    ///     </para>
    ///     <para>
    ///     The two failure modes are wildly asymmetric. Setting this too low silently corrupts data; setting
    ///     it too high only means a genuinely wedged message waits longer before it is redelivered, and holds
    ///     a flow-control slot in the meantime. So the deliberate choice is to be generous: one hour, which is
    ///     sixty times <see cref="WolverineOptions.DefaultExecutionTimeout" /> and therefore leaves ample room
    ///     for a slow handler plus its inline retries. This happens to equal
    ///     <c>SubscriberClient.DefaultMaxTotalAckExtension</c>, so it is not a behavioural change — the point
    ///     is that it is now a value Wolverine chose, is documented, is configurable, and is monitored:
    ///     crossing it is logged at Warning rather than passing in silence.
    ///     </para>
    ///     <para>
    ///     Lower this if you would rather a stuck message be redelivered promptly and your handlers are
    ///     genuinely idempotent under concurrent execution. Raise it if you knowingly run handlers longer
    ///     than an hour.
    ///     </para>
    /// </summary>
    public TimeSpan MaxTotalAckExtension = TimeSpan.FromHours(1);

    public PubsubRetryPolicy RetryPolicy = new();
}

public class PubsubRetryPolicy
{
    public int MaxRetryCount = 5;
    public int RetryDelay = 1000;
}

public class PubsubDeadLetterOptions
{
    public bool Enabled = false;
    public CreateSubscriptionOptions Subscription = new();
    public CreateTopicOptions Topic = new();
}