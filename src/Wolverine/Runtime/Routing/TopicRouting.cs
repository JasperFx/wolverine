using System;
using Baseline.Reflection;
using ImTools;
using Wolverine.Util;
using Wolverine.Attributes;

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

        topic = messageType.HasAttribute<TopicAttribute>()
            ? messageType.GetAttribute<TopicAttribute>()!.TopicName
            : messageType.ToMessageTypeName();

        _topics = _topics.AddOrUpdate(messageType, topic);

        return topic;
    }

    public static string DetermineTopicName(Envelope envelope)
    {
        return envelope.TopicName ?? DetermineTopicName(envelope.Message!.GetType());
    }
}
