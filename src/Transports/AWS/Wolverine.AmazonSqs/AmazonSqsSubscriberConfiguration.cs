using System.Text.Json;
using Amazon.SQS.Model;
using Newtonsoft.Json;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Newtonsoft;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;

namespace Wolverine.AmazonSqs;

public class
    AmazonSqsSubscriberConfiguration : SubscriberConfiguration<AmazonSqsSubscriberConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsSubscriberConfiguration(AmazonSqsQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure how the queue should be created within SQS
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration ConfigureQueueCreation(Action<CreateQueueRequest> configure)
    {
        add(e => configure(e.Configuration));
        return this;
    }

    /// <summary>
    ///     Opt this standard (non-FIFO) queue into Amazon SQS fair queues by mapping
    ///     <see cref="Envelope.GroupId"/> (set through <c>DeliveryOptions.GroupId</c> or message
    ///     partitioning) to the SQS <c>MessageGroupId</c> on outgoing messages. This improves
    ///     fairness for multi-tenant workloads and implies no ordering or deduplication semantics.
    ///     Has no effect on FIFO queues, which always set <c>MessageGroupId</c>. See
    ///     https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html
    /// </summary>
    public AmazonSqsSubscriberConfiguration EnableFairQueueMessageGroups()
    {
        add(e => e.EnableFairQueueMessageGroups = true);
        return this;
    }

    /// <summary>
    ///     Split a message whose body would exceed SQS's hard 256KB limit into several SQS messages, and
    ///     reassemble it on the receiving side. Without this an oversized message is rejected by SQS with
    ///     a permanent <c>SenderFault</c>, and Wolverine discards it rather than retrying forever.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Wolverine to Wolverine only</b>, and reassembly happens <b>in memory on one listener</b>, so
    ///     every fragment of a message has to reach the same node. SQS is a competing-consumer queue, so
    ///     use this only on a FIFO queue, with a <c>GlobalPartitioning</c> listener, or with a single
    ///     listening node. Prefer a claim check (<c>WolverineFx.AmazonS3</c>) otherwise -- it
    ///     is the AWS-sanctioned answer and has none of these constraints.
    ///     </para>
    ///     <para>
    ///     See <a href="https://wolverinefx.net/guide/messaging/transports/sqs/large-messages.html">Large
    ///     messages in SQS</a>.
    ///     </para>
    /// </remarks>
    /// <param name="reassemblyTimeout">
    ///     How long a listener holds an incomplete set of fragments before abandoning it. Defaults to 5
    ///     minutes. Abandoned fragments were never deleted from SQS, so they become visible again.
    /// </param>
    public AmazonSqsSubscriberConfiguration FragmentOversizedMessages(TimeSpan? reassemblyTimeout = null)
    {
        add(e =>
        {
            e.FragmentOversizedMessages = true;
            if (reassemblyTimeout.HasValue)
            {
                e.FragmentReassemblyTimeout = reassemblyTimeout.Value;
            }
        });

        return this;
    }

    /// Opt to send messages as raw JSON without any Wolverine metadata
    /// </summary>
    /// <param name="defaultMessageType">Optional. If both sending and receiving from this queue, you will want to specify a default message type</param>
    /// <param name="configure">Optional configuration of System.Text.Json for this endpoint</param>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration SendRawJsonMessage(Type? defaultMessageType = null, Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions();
        configure?.Invoke(options);
        add(e => e.Mapper = new RawJsonSqsEnvelopeMapper(defaultMessageType ?? typeof(object), options));

        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for SQS interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration InteropWith(ISqsEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }
    
    /// <summary>
    /// Create a completely customized mapper using the WolverineRuntime and the current
    /// Endpoint. This is built lazily at system bootstrapping time
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration UseInterop(Func<AmazonSqsQueue, ISqsEnvelopeMapper> factory)
    {
        add(e => e.Mapper = factory(e));
        return this;
    }

    /// <summary>
    /// Use an NServiceBus compatible enveloper mapper to interact with NServiceBus systems on the other end
    /// </summary>
    /// <returns></returns>
    /// <param name="replyQueueName">Name of an SQS queue where NServiceBus should send resplies back to this application</param>
    public AmazonSqsSubscriberConfiguration UseNServiceBusInterop(string? replyQueueName)
    {
        add(e =>
        {
            e.DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
            e.Mapper = new NServiceBusEnvelopeMapper(replyQueueName!, e);
        });

        return this;
    }

    /// <summary>
    /// Use a MassTransit compatible envelope mapper to interact with MassTransit systems on the other end
    /// </summary>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration UseMassTransitInterop()
    {
        add(e => e.Mapper = new MassTransitMapper((Endpoint as IMassTransitInteropEndpoint)!));
        return this;
    }
    
    /// <summary>
    /// Interop with upstream systems by reading messages with the CloudEvents specification
    /// </summary>
    /// <param name="jsonSerializerOptions"></param>
    /// <returns></returns>
    public AmazonSqsSubscriberConfiguration InteropWithCloudEvents(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        add(e =>
        {
            e.MapperFactory = (queue, r) =>
            {
                var mapper = e.BuildCloudEventsMapper(r, jsonSerializerOptions);
                e.DefaultSerializer = mapper;
                return new CloudEventsSqsMapper(mapper);
            };
        });

        return this;
    }
}