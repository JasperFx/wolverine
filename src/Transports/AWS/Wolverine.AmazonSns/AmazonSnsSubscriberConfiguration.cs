using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;

namespace Wolverine.AmazonSns;

public class
    AmazonSnsSubscriberConfiguration : SubscriberConfiguration<AmazonSnsSubscriberConfiguration, AmazonSnsTopic>
{
    internal AmazonSnsSubscriberConfiguration(AmazonSnsTopic endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure how the topic should be created within SNS
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration ConfigureTopicCreation(Action<CreateTopicRequest> configure)
    {
        add(e => configure(e.Configuration));
        return this;
    }

    /// Opt to send messages as raw JSON without any Wolverine metadata
    /// </summary>
    /// <param name="defaultMessageType">Optional. If both sending and receiving from this queue, you will want to specify a default message type</param>
    /// <param name="configure">Optional configuration of System.Text.Json for this endpoint</param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration SendRawJsonMessage(Type? defaultMessageType = null,
        Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions();
        configure?.Invoke(options);
        add(e => e.Mapper = new RawJsonSnsEnvelopeMapper(defaultMessageType ?? typeof(object), options));

        return this;
    }

    /// <summary>
    /// Utilize custom envelope mapping for SNS interoperability with external non-Wolverine systems
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public AmazonSnsSubscriberConfiguration InteropWith(ISnsEnvelopeMapper mapper)
    {
        add(e => e.Mapper = mapper);
        return this;
    }
}
