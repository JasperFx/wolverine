using System.Text.Json;
using Amazon.SQS.Model;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;

namespace Wolverine.AmazonSqs;

public class AmazonSqsListenerConfiguration : ListenerConfiguration<AmazonSqsListenerConfiguration, AmazonSqsQueue>
{
    internal AmazonSqsListenerConfiguration(AmazonSqsQueue endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Completely disable all SQS dead letter queueing for just this queue
    /// </summary>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration DisableDeadLetterQueueing()
    {
        add(e => e.DeadLetterQueueName = null);
        return this;
    }

    /// <summary>
    ///     Customize the dead letter queueing for just this queue
    /// </summary>
    /// <param name="deadLetterQueue"></param>
    /// <param name="configure">Optionally configure properties of the dead letter queue itself</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public AmazonSqsListenerConfiguration ConfigureDeadLetterQueue(string deadLetterQueue,
        Action<AmazonSqsQueue>? configure = null)
    {
        if (deadLetterQueue == null)
        {
            throw new ArgumentNullException(nameof(deadLetterQueue));
        }

        add(e =>
        {
            e.DeadLetterQueueName = AmazonSqsTransport.SanitizeSqsName(deadLetterQueue);
            if (configure != null)
            {
                e.ConfigureDeadLetterQueue(configure);
            }
        });

        return this;
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });

        return this;
    }

    /// <summary>
    ///     Configure how the queue should be created within SQS
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration ConfigureQueueCreation(Action<CreateQueueRequest> configure)
    {
        add(e => configure(e.Configuration));
        return this;
    }

    /// <summary>
    /// Configure this listener to receive raw JSON of an expected message type from
    /// an external system
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration ReceiveRawJsonMessage(
        Type messageType,
        Action<JsonSerializerOptions>? configure = null)
    {
        add(e =>
        {
            JsonSerializerOptions serializerOptions = new JsonSerializerOptions();

            configure?.Invoke(serializerOptions);

            e.Mapper = new RawJsonSqsEnvelopeMapper(messageType, serializerOptions);
        });

        return this;
    }
    
    /// <summary>
    ///     Configure this listener to receive a message from an SNS topic subscription
    /// </summary>
    /// <param name="internalMessageMapper">The mapper for message forwarded from the SNS topic</param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration ReceiveSnsTopicMessage(ISqsEnvelopeMapper? internalMessageMapper = null)
    {
        add(e =>
        {
            internalMessageMapper ??= new DefaultSqsEnvelopeMapper();
            e.Mapper = new SnsTopicEnvelopeMapper(internalMessageMapper);
        });

        return this;
    } 

    /// <summary>
    /// Utilize custom envelope mapping for SQS interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AmazonSqsListenerConfiguration InteropWith(ISqsEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }
}
