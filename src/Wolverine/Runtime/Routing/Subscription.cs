using System;
using System.Linq;
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
    private string[] _contentTypes = { EnvelopeConstants.JsonContentType };

    public Subscription()
    {
    }

    public Subscription(Assembly assembly)
    {
        Scope = RoutingScope.Assembly;
        Match = assembly.GetName().Name;
    }

    /// <summary>
    ///     How does this rule apply? For all messages? By Namespace? By Assembly?
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public RoutingScope Scope { get; set; } = RoutingScope.All;


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
    public string? Match { get; set; } = string.Empty;


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
            Match = type.FullName
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
            RoutingScope.Assembly => type.Assembly.GetName().Name!.EqualsIgnoreCase(Match!),
            RoutingScope.Namespace => type.IsInNamespace(Match!),
            RoutingScope.Type => type.Name.EqualsIgnoreCase(Match!) || type.FullName!.EqualsIgnoreCase(Match!) ||
                                 type.ToMessageTypeName().EqualsIgnoreCase(Match!),
            RoutingScope.TypeName => type.ToMessageTypeName().EqualsIgnoreCase(Match!),
            RoutingScope.Implements => type.CanBeCastTo(BaseType),
            _ => !type.CanBeCastTo<IAgentCommand>() && !type.CanBeCastTo<IInternalMessage>()
        };
    }
    
    public Type BaseType { get; set; }


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
            hashCode = (hashCode * 397) ^ (Match != null ? Match.GetHashCode() : 0);
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