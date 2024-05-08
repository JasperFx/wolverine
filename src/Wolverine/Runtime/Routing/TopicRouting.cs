using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Util;

namespace Wolverine.Runtime.Routing;

internal static class TopicRouting
{
    private static ImHashMap<Type, string> _topics = ImHashMap<Type, string>.Empty;

    public static string DetermineTopicName(Type messageType)
    {
        if (_topics.TryFind(messageType, out var topic))
        {
            return topic;
        }

        topic = messageType.TryGetAttribute<TopicAttribute>(out var attribute) ?
            attribute.TopicName :
            messageType.ToMessageTypeName();

        _topics = _topics.AddOrUpdate(messageType, topic);

        return topic;
    }

    public static string DetermineTopicName(Envelope envelope)
    {
        if (envelope.TopicName.IsNotEmpty()) return envelope.TopicName;

        if (envelope.Message == null)
            throw new ArgumentNullException(nameof(envelope),
                $"{nameof(envelope.Message)} is null, making this operation invalid");

        return envelope.TopicName ?? DetermineTopicName(envelope.Message?.GetType());
    }
}

public class TopicRoutingRule : IEnvelopeRule
{
    public void Modify(Envelope envelope)
    {
        envelope.TopicName ??= TopicRouting.DetermineTopicName(envelope);
    }
}