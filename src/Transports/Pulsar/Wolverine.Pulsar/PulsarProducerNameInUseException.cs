namespace Wolverine.Pulsar;

/// <summary>
/// GH-5847. The Pulsar broker kept refusing a Wolverine producer because another producer with the same name was
/// already connected to the topic (ProducerBusy). With <c>EnableDeduplication()</c> producers are named, and Pulsar
/// allows one connected producer per name on a topic, so several running instances of one service that publish to
/// the same deduplicated topic need a producer name each.
/// </summary>
public sealed class PulsarProducerNameInUseException : Exception
{
    public PulsarProducerNameInUseException(string producerName, string topic, TimeSpan refusedFor,
        Exception? innerException = null)
        : base(buildMessage(producerName, topic, refusedFor), innerException)
    {
        ProducerName = producerName;
        Topic = topic;
        RefusedFor = refusedFor;
    }

    /// <summary>
    /// The producer name the broker refused
    /// </summary>
    public string ProducerName { get; }

    /// <summary>
    /// The topic the producer was connecting to
    /// </summary>
    public string Topic { get; }

    /// <summary>
    /// How long the broker had been refusing the producer when Wolverine stopped retrying
    /// </summary>
    public TimeSpan RefusedFor { get; }

    private static string buildMessage(string producerName, string topic, TimeSpan refusedFor)
    {
        return
            $"The Pulsar broker refused producer '{producerName}' on topic '{topic}' for {refusedFor.TotalSeconds:0} seconds because another producer with that name is already connected (ProducerBusy). " +
            "With EnableDeduplication(), every running instance of a service that publishes to the same topic needs its own producer name. " +
            "Call UsePulsar(...).ProducerNamePerInstance(\"<stable instance id>\") to append an instance id to Wolverine's producer names, " +
            "or set a distinct name per instance through EnableDeduplication(producerName) or ConfigureProducer(). " +
            "Deduplication then works per instance.";
    }
}
