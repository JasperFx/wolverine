using System.Text.RegularExpressions;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

/// <summary>
/// Represents a single Wolverine listener that subscribes to multiple Kafka topics
/// using one Kafka consumer. This reduces consumer group rebalances and startup time
/// when many topics share the same logical workload.
/// </summary>
public class KafkaTopicGroup : KafkaTopic, IBrokerEndpoint
{
    /// <summary>
    /// The Kafka topic names this endpoint subscribes to
    /// </summary>
    public string[] TopicNames { get; }

    public KafkaTopicGroup(KafkaTransport parent, string[] topicNames, EndpointRole role)
        : base(parent, SanitizeGroupName(string.Join("_", topicNames)), role)
    {
        TopicNames = topicNames;
        EndpointName = string.Join("_", topicNames);
    }

    internal static string SanitizeGroupName(string name)
    {
        // Replace any characters that are not valid in a URI path segment
        return Regex.Replace(name, @"[^a-zA-Z0-9_\-.]", "_");
    }

    public override bool AutoStartSendingAgent() => false;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        var config = GetEffectiveConsumerConfig();

        if (Mode == EndpointMode.Durable)
        {
            config.EnableAutoCommit = false;
        }

        var listener = new KafkaTopicGroupListener(this, config,
            Parent.CreateConsumer(config), receiver, runtime.LoggerFactory.CreateLogger<KafkaTopicGroupListener>());
        return ValueTask.FromResult((IListener)listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException("KafkaTopicGroup is a listen-only endpoint. Use KafkaTopic for publishing.");
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (NativeDeadLetterQueueEnabled)
        {
            var dlqTopic = Parent.Topics[Parent.DeadLetterQueueTopicName];
            dlqTopic.EnvelopeMapper ??= dlqTopic.BuildMapper(runtime);
            deadLetterSender = new InlineKafkaSender(dlqTopic);
            return true;
        }

        deadLetterSender = default;
        return false;
    }

    // Re-implement IBrokerEndpoint for multi-topic support

    new public async ValueTask<bool> CheckAsync()
    {
        if (Parent.Usage == KafkaUsage.ConsumeOnly) return true;

        try
        {
            using var client = Parent.CreateProducer(Parent.ProducerConfig);
            foreach (var topicName in TopicNames)
            {
                await client.ProduceAsync(topicName, new Message<string, byte[]>
                {
                    Key = "ping",
                    Value = System.Text.Encoding.Default.GetBytes("ping")
                });
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    new public async ValueTask TeardownAsync(ILogger logger)
    {
        using var adminClient = Parent.CreateAdminClient();
        await adminClient.DeleteTopicsAsync(TopicNames);
    }

    /// <summary>
    /// Optional action to configure the TopicSpecification for each topic created by this group.
    /// The string parameter is the topic name. Applied before topic creation unless <see cref="CreateTopicFunc"/> is overridden.
    /// </summary>
    public Action<string, TopicSpecification>? SpecificationConfig { get; set; }

    /// <summary>
    /// Optional override for topic creation logic. Receives the admin client and topic name.
    /// Defaults to creating the topic using <see cref="SpecificationConfig"/> if set.
    /// </summary>
    public new Func<IAdminClient, string, Task>? CreateTopicFunc { get; set; }

    new public async ValueTask SetupAsync(ILogger logger)
    {
        using var adminClient = Parent.CreateAdminClient();

        foreach (var topicName in TopicNames)
        {
            try
            {
                if (CreateTopicFunc != null)
                {
                    await CreateTopicFunc(adminClient, topicName);
                }
                else
                {
                    var spec = new TopicSpecification { Name = topicName };
                    SpecificationConfig?.Invoke(topicName, spec);
                    await adminClient.CreateTopicsAsync([spec]);
                }

                logger.LogInformation("Created Kafka topic {Topic}", topicName);
            }
            catch (CreateTopicsException e)
            {
                if (e.Message.Contains("already exists.")) continue;
                throw;
            }
        }
    }
}
