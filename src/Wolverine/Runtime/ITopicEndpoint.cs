namespace Wolverine.Runtime;

/// <summary>
/// Marker interface that just tells Wolverine this endpoint
/// is a topic for brokers that support topic-based routing.
///
/// Tells Wolverine that this endpoint *might* have subscriptions
/// later
/// </summary>
public interface ITopicEndpoint;