using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;

namespace Wolverine.Pubsub;

public static class PubsubTransportExtensions {

	/// <summary>
	///     Quick access to the Google Cloud Pub/Sub Transport within this application.
	///     This is for advanced usage
	/// </summary>
	/// <param name="endpoints"></param>
	/// <returns></returns>
	internal static PubsubTransport PubsubTransport(this WolverineOptions endpoints) {
		var transports = endpoints.As<WolverineOptions>().Transports;

		return transports.GetOrCreate<PubsubTransport>();
	}

	/// <summary>
	/// Additive configuration to the Google Cloud Pub/Sub integration for this Wolverine application
	/// </summary>
	/// <param name="endpoints"></param>
	/// <returns></returns>
	public static PubsubConfiguration ConfigurePubsub(this WolverineOptions endpoints) => new PubsubConfiguration(endpoints.PubsubTransport(), endpoints);

	/// <summary>
	/// Connect to Google Cloud Pub/Sub with a prject id
	/// </summary>
	/// <param name="endpoints"></param>
	/// <param name="projectId"></param>
	/// <param name="configure"></param>
	/// <returns></returns>
	/// <exception cref="ArgumentNullException"></exception>
	public static PubsubConfiguration UsePubsub(this WolverineOptions endpoints, string projectId, Action<PubsubTransportOptions>? configure = null) {
		var transport = endpoints.PubsubTransport();

		transport.ProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));

		configure?.Invoke(transport.Options);

		return new PubsubConfiguration(transport, endpoints);
	}

	/// <summary>
	///     Listen for incoming messages at the designated Google Cloud Pub/Sub topic by name
	/// </summary>
	/// <param name="endpoints"></param>
	/// <param name="topicName">The name of the Google Cloud Pub/Sub topic</param>
	/// <param name="configure">
	///     Optional configuration for this Google Cloud Pub/Sub subscription if being initialized by Wolverine
	///     <returns></returns>
	public static PubsubSubscriptionConfiguration ListenToPubsubTopic(
		this WolverineOptions endpoints,
		string topicName,
		Action<PubsubSubscription>? configure = null
	) {
		var transport = endpoints.PubsubTransport();
		var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

		topic.EndpointName = topicName;

		var subscription = topic.FindOrCreateSubscription();

		configure?.Invoke(subscription);

		return new PubsubSubscriptionConfiguration(subscription);
	}

	/// <summary>
	/// Publish the designated messages to a Google Cloud Pub/Sub topic
	/// </summary>
	/// <param name="publishing"></param>
	/// <param name="topicName"></param>
	/// <returns></returns>
	public static PubsubTopicConfiguration ToPubsubTopic(
		this IPublishToExpression publishing,
		string topicName
	) {
		var transports = publishing.As<PublishingExpression>().Parent.Transports;
		var transport = transports.GetOrCreate<PubsubTransport>();
		var topic = transport.Topics[transport.MaybeCorrectName(topicName)];

		topic.EndpointName = topicName;

		// This is necessary unfortunately to hook up the subscription rules
		publishing.To(topic.Uri);

		return new PubsubTopicConfiguration(topic);
	}
}
