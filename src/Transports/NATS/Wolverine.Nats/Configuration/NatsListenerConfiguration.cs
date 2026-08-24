using NATS.Client.JetStream.Models;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;

namespace Wolverine.Nats.Configuration;

public class NatsListenerConfiguration
    : ListenerConfiguration<NatsListenerConfiguration, NatsEndpoint>
{
    public NatsListenerConfiguration(NatsEndpoint endpoint)
        : base(endpoint) { }

    /// <summary>
    /// For a Durable JetStream listener: the most consumed messages the subscriber coalesces (for at
    /// most 5ms) into one batched inbox insert instead of one insert per message -- the same knob the
    /// RabbitMQ and Kafka listeners have. 1 reverts to strict message-at-a-time persistence. Ignored
    /// for Buffered/Inline endpoints and for core NATS. Default 100. See GH-4026.
    /// </summary>
    public NatsListenerConfiguration MaximumMessagesToReceive(int maximum)
    {
        if (maximum < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "Must be at least 1");
        }

        add(e => e.MaximumMessagesToReceive = maximum);
        return this;
    }

    /// <summary>
    /// Use JetStream for durable messaging
    /// </summary>
    public NatsListenerConfiguration UseJetStream(
        string? streamName = null,
        string? consumerName = null
    )
    {
        add(endpoint =>
        {
            endpoint.UseJetStream = true;
            endpoint.StreamName = streamName ?? endpoint.Subject.Replace(".", "_").ToUpper();
            endpoint.ConsumerName = consumerName;
        });

        return this;
    }

    /// <summary>
    /// GH-4053. Override the JetStream consumer's <c>AckWait</c> for this listener -- how long the server waits
    /// for an unacknowledged delivery before redelivering it. Defaults to the transport-wide
    /// <c>JetStreamDefaults.AckWait</c> (30 seconds).
    ///
    /// <para>
    /// Under <c>ProcessInParallelWithNativeAcks()</c> this is the lease Wolverine renews with <c>AckProgress</c>
    /// for as long as an envelope sits in an execution lane, and the renewal tick is half of it. Shortening it
    /// makes a genuinely dead node's messages come back faster, at the cost of more renewal round trips.
    /// </para>
    /// </summary>
    public NatsListenerConfiguration AckWait(TimeSpan ackWait)
    {
        if (ackWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ackWait), "Must be greater than zero");
        }

        add(e => e.AckWait = ackWait);
        return this;
    }

    /// <summary>
    /// GH-4053. The longest a single delivery may be kept alive by <c>AckProgress</c> renewals under
    /// <c>ProcessInParallelWithNativeAcks()</c>, measured from its receipt. Past this Wolverine stops renewing and
    /// lets JetStream redeliver, so one wedged handler cannot pin a <c>MaxAckPending</c> slot forever.
    /// Default 12 hours.
    /// </summary>
    public NatsListenerConfiguration MaximumAckExtension(TimeSpan maximum)
    {
        add(e => e.MaximumAckExtension = maximum);
        return this;
    }

    /// <summary>
    /// GH-4053. Override the JetStream consumer's <c>MaxAckPending</c> -- the number of deliveries the server
    /// leaves unacknowledged before it stops delivering. This is JetStream's prefetch equivalent.
    ///
    /// <para>
    /// Under <c>ProcessInParallelWithNativeAcks()</c> it defaults to twice the number of lanes that can be busy at
    /// once (the <c>PartitionProcessingByGroupId()</c> slot count, otherwise <c>MaximumParallelMessages()</c>), and
    /// it must cover every one of them: sized lower, the consumer stops delivering while lanes sit idle. Every
    /// other mode leaves the NATS server default of 1,000 alone.
    /// </para>
    /// </summary>
    public NatsListenerConfiguration MaxAckPending(int maximum)
    {
        if (maximum < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "Must be at least 1");
        }

        add(e => e.MaxAckPending = maximum);
        return this;
    }

    /// <summary>
    /// Use a queue group for load balancing (Core NATS only)
    /// </summary>
    public NatsListenerConfiguration UseQueueGroup(string queueGroup)
    {
        add(endpoint =>
        {
            endpoint.QueueGroup = queueGroup;
        });

        return this;
    }

    /// <summary>
    /// Configure dead letter queue settings for this NATS listener
    /// </summary>
    public NatsListenerConfiguration ConfigureDeadLetterQueue(
        int maxDeliveryAttempts,
        string? deadLetterSubject = null
    )
    {
        add(endpoint =>
        {
            endpoint.DeadLetterQueueEnabled = true;
            endpoint.DeadLetterSubject = deadLetterSubject;
            endpoint.MaxDeliveryAttempts = maxDeliveryAttempts;
        });

        return this;
    }

    /// <summary>
    /// Disable dead letter queue handling for this listener
    /// </summary>
    public NatsListenerConfiguration DisableDeadLetterQueueing()
    {
        add(endpoint =>
        {
            endpoint.DeadLetterQueueEnabled = false;
        });

        return this;
    }

    /// <summary>
    /// Configure the dead letter subject for failed messages
    /// </summary>
    public NatsListenerConfiguration DeadLetterTo(string deadLetterSubject)
    {
        add(endpoint =>
        {
            endpoint.DeadLetterSubject = deadLetterSubject;
        });

        return this;
    }

    /// <summary>
    /// Override the JetStream consumer's <c>DeliverPolicy</c> for this listener
    /// only — wins over any transport-wide default set via
    /// <c>UseJetStream(d =&gt; d.DeliverPolicy = ...)</c>.
    ///
    /// Use <see cref="ConsumerConfigDeliverPolicy.New"/> to start an
    /// auto-provisioned consumer at "only messages that arrive after this
    /// consumer is created" — the typical answer when standing up a new
    /// listener against an existing stream you don't want to replay from the
    /// beginning. Other useful values include
    /// <see cref="ConsumerConfigDeliverPolicy.Last"/> ("only the latest
    /// message"), <see cref="ConsumerConfigDeliverPolicy.LastPerSubject"/>
    /// (compaction-style: latest per subject filter), and the explicit
    /// <see cref="ConsumerConfigDeliverPolicy.All"/> (the NATS-server default
    /// when nothing is configured — replay every message currently in the
    /// stream). For <see cref="ConsumerConfigDeliverPolicy.ByStartSequence"/>
    /// or <see cref="ConsumerConfigDeliverPolicy.ByStartTime"/> you must
    /// pre-create the consumer outside Wolverine and reference it by name in
    /// <c>UseJetStream(...)</c> — the supplemental
    /// <c>OptStartSeq</c> / <c>OptStartTime</c> properties have no
    /// listener-configuration surface here.
    ///
    /// Only applies to consumers Wolverine itself auto-provisions; if you
    /// reference a pre-created consumer by name via
    /// <c>UseJetStream(streamName, consumerName)</c>, Wolverine will reuse
    /// that consumer's existing config and ignore this override (matches the
    /// existing reuse-by-name behaviour in <c>JetStreamSubscriber</c>).
    /// </summary>
    public NatsListenerConfiguration DeliverFrom(ConsumerConfigDeliverPolicy deliverPolicy)
    {
        add(endpoint =>
        {
            endpoint.DeliverPolicy = deliverPolicy;
        });

        return this;
    }
}
