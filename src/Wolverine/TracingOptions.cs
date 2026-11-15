using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine;

/// <summary>
/// GH-5537. Marker interface for a message type that should always start a <b>new trace</b> on the
/// receiving side, with a span link back to the activity that sent it. The same as decorating the
/// type with <see cref="StartsNewLinkedTraceAttribute" /> or registering it through
/// <see cref="TracingOptions.StartNewLinkedTraceFor{T}" />. An explicit
/// <see cref="DeliveryOptions.StartsNewLinkedTrace" /> on a send still wins.
/// </summary>
public interface IStartsNewLinkedTrace;

/// <summary>
/// GH-5537. Application wide OpenTelemetry tracing rules for outgoing messages.
/// </summary>
public class TracingOptions
{
    private readonly List<Func<Type, bool>> _linkedTraceFilters = new();

    /// <summary>
    /// Every outgoing message that can be cast to <typeparamref name="T" /> starts a new trace on the
    /// receiving side, with a span link back to the sending activity, on every route (local, external
    /// and cascaded). An explicit <see cref="DeliveryOptions.StartsNewLinkedTrace" /> on a send wins
    /// over this rule.
    /// </summary>
    public TracingOptions StartNewLinkedTraceFor<T>()
    {
        return StartNewLinkedTraceFor(type => type.CanBeCastTo<T>());
    }

    /// <summary>
    /// Every outgoing message whose type matches <paramref name="filter" /> starts a new trace on the
    /// receiving side, with a span link back to the sending activity. The filter is evaluated once per
    /// message type and route, never per message. An explicit
    /// <see cref="DeliveryOptions.StartsNewLinkedTrace" /> on a send wins over this rule.
    /// </summary>
    public TracingOptions StartNewLinkedTraceFor(Func<Type, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _linkedTraceFilters.Add(filter);
        return this;
    }

    /// <summary>
    /// The rules that apply to this message type, resolved once per route rather than per message.
    /// </summary>
    internal IEnumerable<IEnvelopeRule> RulesFor(Type messageType)
    {
        foreach (var filter in _linkedTraceFilters)
        {
            if (filter(messageType))
            {
                yield return StartsNewLinkedTraceRule.Instance;
                yield break;
            }
        }
    }
}

internal sealed class StartsNewLinkedTraceRule : IEnvelopeRule
{
    public static readonly StartsNewLinkedTraceRule Instance = new();

    public void Modify(Envelope envelope)
    {
        envelope.StartsNewLinkedTrace = true;
    }

    public override string ToString()
    {
        return "Starts a new linked trace";
    }
}
