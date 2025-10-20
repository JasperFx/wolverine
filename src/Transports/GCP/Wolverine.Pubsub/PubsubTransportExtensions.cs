using JasperFx.Core;
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
	/// <param name="subscriptionName">The name of the subscription to this topic name. If not specified, Wolverine will use the topic name</param>
	/// <param name="topicMessageRetention">Optionally override the topic's message retention if Wolverine is declaring the topics and subscriptions</param>
	/// <returns></returns>
	public static PubsubTopicListenerConfiguration ListenToPubsubSubscription(
        this WolverineOptions endpoints,
        string topicName,
        string? subscriptionName = null,
        TimeSpan? topicMessageRetention = null
    )
	{
		if (topicName.IsEmpty()) throw new ArgumentNullException(nameof(topicName));

		subscriptionName ??= topicName;
		if (subscriptionName.IsEmpty()) throw new ArgumentNullException(nameof(subscriptionName));
	    
        var transport = endpoints.PubsubTransport();
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        if (topicMessageRetention != null)
        {
	        topic.MessageRetentionDuration = topicMessageRetention.Value;
        }

        topic.EndpointName = topicName;
        
        var subscription = topic.GcpSubscriptions[transport.MaybeCorrectName(subscriptionName)];

        return new PubsubTopicListenerConfiguration(subscription);
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
        string topicName
    )
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PubsubTransport>();
        var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

        topic.EndpointName = topicName;

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(topic.Uri);

        return new PubsubTopicSubscriberConfiguration(topic);
    }
}