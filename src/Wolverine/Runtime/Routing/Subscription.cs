using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Wolverine.Runtime.Agents;
using Wolverine.Util;

namespace Wolverine.Runtime.Routing;

public class Subscription
{
    private string[] _contentTypes = [EnvelopeConstants.JsonContentType];

    public Subscription()
    {
    }

    public Subscription(Assembly assembly)
    {
        Scope = RoutingScope.Assembly;
        Match = assembly.GetName().Name!;
    }

    /// <summary>
    ///     How does this rule apply? For all messages? By Namespace? By Assembly?
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public RoutingScope Scope { get; init; } = RoutingScope.All;


    /// <summary>
    ///     The legal, accepted content types for the receivers. The default is [EnvelopeConstants.JsonContentType]
    /// </summary>
    public string[] ContentTypes
    {
        get => _contentTypes;
        set => _contentTypes = value.Distinct().ToArray();
    }

    /// <summary>
    ///     A type name or namespace name if matching on type or namespace
    /// </summary>
    public string Match { get; init; } = string.Empty;

    public Type? BaseType { get; init; }

    /// <summary>
    /// True when this subscription was added by an <see cref="IMessageRoutingConvention"/>
    /// pre-registering a sender (so endpoint policies like
    /// <c>UseDurableOutboxOnAllSendingEndpoints</c> can see <c>Subscriptions.Any() == true</c>
    /// before the broker connects). Routing precedence layers that exist to honour
    /// <em>explicit</em> publish rules — chiefly <c>ExplicitRouting</c> — must ignore these
    /// so they don't short-circuit past local handler routing or override user-wired routes.
    /// See GH-2588 (the original outbox fix that introduced pre-registration) and the
    /// follow-up MessageRoutingTests regression that motivated this flag.
    /// </summary>
    internal bool IsFromConvention { get; init; }

    /// <summary>
    ///     Create a subscription for a specific message type
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    public static Subscription ForType(Type type)
    {
        return new Subscription
        {
            Scope = RoutingScope.Type,
            Match = type.FullName!
        };
    }

    /// <summary>
    /// Create a subscription for a specific message type that was added by a routing
    /// convention as part of pre-registering a sender endpoint. See <see cref="IsFromConvention"/>.
    /// </summary>
    internal static Subscription ForConventionalType(Type type)
    {
        return new Subscription
        {
            Scope = RoutingScope.Type,
            Match = type.FullName!,
            IsFromConvention = true
        };
    }

    /// <summary>
    ///     Create a subscription for all messages published in this application
    /// </summary>
    /// <returns></returns>
    public static Subscription All()
    {
        return new Subscription
        {
            Scope = RoutingScope.All
        };
    }

    public bool Matches(Type type)
    {
        return Scope switch
        {
            RoutingScope.Assembly => type.Assembly.GetName().Name!.EqualsIgnoreCase(Match),
            RoutingScope.Namespace => type.IsInNamespace(Match),
            RoutingScope.Type => type.Name.EqualsIgnoreCase(Match) || type.FullName!.EqualsIgnoreCase(Match) ||
                                 type.ToMessageTypeName().EqualsIgnoreCase(Match),
            RoutingScope.TypeName => type.ToMessageTypeName().EqualsIgnoreCase(Match),
            RoutingScope.Implements => type.CanBeCastTo(BaseType!),
            _ => !type.CanBeCastTo<IAgentCommand>()
        };
    }

    protected bool Equals(Subscription other)
    {
        return Scope == other.Scope && ContentTypes.SequenceEqual(other.ContentTypes) &&
               string.Equals(Match, other.Match);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((Subscription)obj);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = (int)Scope;
            hashCode = (hashCode * 397) ^ ContentTypes.GetHashCode();
            hashCode = (hashCode * 397) ^ Match.GetHashCode();
            return hashCode;
        }
    }

    public override string ToString()
    {
        switch (Scope)
        {
            case RoutingScope.All:
                return "All Messages";

            case RoutingScope.Assembly:
                return $"Message assembly is {Match}";

            case RoutingScope.Namespace:
                return $"Message type is within namespace {Match}";

            case RoutingScope.Type:
                return $"Message type is {Match}";

            case RoutingScope.TypeName:
                return $"Message name is '{Match}'";
        }

        throw new ArgumentOutOfRangeException();
    }
}