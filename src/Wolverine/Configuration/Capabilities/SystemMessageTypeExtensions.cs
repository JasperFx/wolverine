using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Centralized predicate for deciding whether a given message type is a system /
/// framework-internal type that should be excluded from observability surfaces:
/// <see cref="ServiceCapabilities"/>, <c>IWolverineObserver.MessageRouted</c>, and
/// <c>IWolverineObserver.MessageCausedBy</c>.
/// </summary>
internal static class SystemMessageTypeExtensions
{
    /// <summary>
    /// Returns true if the supplied message type should be hidden from observability.
    /// Catches:
    /// <list type="bullet">
    ///   <item>Types implementing <see cref="IInternalMessage"/> (preferred — fastest)</item>
    ///   <item>Types implementing <see cref="IAgentCommand"/> (Wolverine internal commands)</item>
    ///   <item>Types implementing <see cref="INotToBeRouted"/> (covers <see cref="ISideEffect"/>,
    ///   <c>ICritterWatchMessage</c>, acknowledgements, etc.)</item>
    ///   <item>Types declared in an assembly marked with <see cref="ExcludeFromServiceCapabilitiesAttribute"/></item>
    /// </list>
    /// </summary>
    public static bool IsSystemMessageType(this Type? messageType)
    {
        if (messageType is null) return false;

        // Marker-interface checks first — these are constant-time runtime type tests.
        if (messageType.CanBeCastTo<IInternalMessage>()) return true;
        if (messageType.CanBeCastTo<IAgentCommand>()) return true;
        if (messageType.CanBeCastTo<INotToBeRouted>()) return true;

        // Assembly-level opt-out — slower, falls through to reflection but cached by the runtime.
        if (messageType.Assembly.HasAttribute<ExcludeFromServiceCapabilitiesAttribute>()) return true;

        return false;
    }
}
