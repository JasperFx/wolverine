using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Pubsub;

public static class PubsubTransportExtensions
{
    /// <summary>
    ///     Quick access to the Google Cloud Platform Pub/Sub Transport within this application.
    ///     This is for advanced usage.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static PubsubTransport PubsubTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<PubsubTransport>();
    }

    /// <summary>
    ///     Additive configuration to the Google Cloud Platform Pub/Sub integration for this Wolverine application.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    public static PubsubConfiguration ConfigurePubsub(this WolverineOptions endpoints)
    {
        return new PubsubConfiguration(endpoints.PubsubTransport(), endpoints);
    }

    /// <summary>
    ///     Connect to Google Cloud Platform Pub/Sub with a project id.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="projectId"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static PubsubConfiguration UsePubsub(this WolverineOptions endpoints, string projectId,
        Action<PubsubTransport>? configure = null)
    {
        var transport = endpoints.PubsubTransport();

        transport.ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));

        configure?.Invoke(transport);

        return new PubsubConfiguration(transport, endpoints);
    }

    /// <summary>
    ///     Listen for incoming messages at the designated Google Cloud Platform Pub/Sub topic by name.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="topicName">The name of the Google Cloud Platform Pub/Sub topic</param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicListenerConfiguration ListenToPubsubTopic(
        this WolverineOptions endpoints,
        string topicName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transport = endpoints.PubsubTransport();
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;
        topic.IsListener = true;

        configure?.Invoke(topic);

        return new PubsubTopicListenerConfiguration(topic);
    }

    /// <summary>
    ///     Listen for incoming messages on an existing Google Cloud Platform Pub/Sub subscription.
    ///     Since an existing subscription is used, IdentifierPrefix is not applied to the subscription name.
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="subscriptionName">The name of the Google Cloud Platform Pub/Sub subscription</param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicListenerConfiguration ListenToPubsubSubscription(
        this WolverineOptions endpoints,
        string subscriptionName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transport = endpoints.PubsubTransport();
        var topic = transport.Topics[subscriptionName];

        topic.IsListener = true;
        topic.IsExistingSubscription = true;

        configure?.Invoke(topic);

        return new PubsubTopicListenerConfiguration(topic);
    }

    /// <summary>
    ///     Publish the designated messages to a Google Cloud Platform Pub/Sub topic.
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicName"></param>
    /// <param name="configure">
    ///     Optional configuration for this Google Cloud Platform Pub/Sub endpoint.
    /// </param>
    /// <returns></returns>
    public static PubsubTopicSubscriberConfiguration ToPubsubTopic(
        this IPublishToExpression publishing,
        string topicName,
        Action<PubsubEndpoint>? configure = null
    )
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PubsubTransport>();
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;

        configure?.Invoke(topic);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new PubsubTopicSubscriberConfiguration(topic);
    }
}