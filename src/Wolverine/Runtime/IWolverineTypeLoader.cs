using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

/// <summary>
/// Implemented by source generators to provide compile-time discovery
/// of handlers, message types, and endpoints. When registered in DI,
/// Wolverine will use this instead of runtime assembly scanning during
/// startup, dramatically reducing cold start time.
///
/// If no IWolverineTypeLoader is registered, Wolverine falls back to
/// its current runtime assembly scanning behavior with zero regression.
/// </summary>
public interface IWolverineTypeLoader
{
    /// <summary>
    /// Handler types discovered at compile time. These are classes matching
    /// Wolverine handler conventions: *Handler/*Consumer suffix, implementing
    /// IWolverineHandler, decorated with [WolverineHandler], or Saga types.
    /// </summary>
    IReadOnlyList<Type> DiscoveredHandlerTypes { get; }

    /// <summary>
    /// Message types discovered at compile time, with their serialization aliases.
    /// Includes types implementing IMessage, decorated with [WolverineMessage],
    /// and types used as parameters in handler methods.
    /// </summary>
    IReadOnlyList<(Type MessageType, string Alias)> DiscoveredMessageTypes { get; }

    /// <summary>
    /// HTTP endpoint types discovered at compile time. These are classes matching
    /// Wolverine.HTTP conventions: *Endpoint/*Endpoints suffix or containing
    /// methods with [WolverineGet], [WolverinePost], etc. attributes.
    /// </summary>
    IReadOnlyList<Type> DiscoveredHttpEndpointTypes { get; }

    /// <summary>
    /// Extension types discovered at compile time from assemblies marked with
    /// [WolverineModule]. Returns the IWolverineExtension implementation types
    /// in dependency order.
    /// </summary>
    IReadOnlyList<Type> DiscoveredExtensionTypes { get; }

    /// <summary>
    /// Whether this loader includes a pre-generated handler type dictionary
    /// that can replace the linear scan in AttachTypesSynchronously.
    /// </summary>
    bool HasPreGeneratedHandlers { get; }

    /// <summary>
    /// A dictionary mapping handler chain TypeName to the pre-generated Type,
    /// enabling O(1) lookup instead of O(N) assembly scanning in AttachTypesSynchronously.
    /// Returns null if no pre-generated handler types are available.
    /// </summary>
    IReadOnlyDictionary<string, Type>? PreGeneratedHandlerTypes { get; }

    /// <summary>
    /// Look up a pre-generated handler type by its generated class name.
    /// Returns null if the type name is not in the pre-generated manifest.
    /// This replaces the O(N) scan of assembly.ExportedTypes with an O(1) dictionary lookup.
    /// </summary>
    /// <param name="typeName">The generated handler type name (e.g., "PlaceOrderHandler")</param>
    /// <returns>The pre-generated Type, or null if not found</returns>
    Type? TryFindPreGeneratedType(string typeName);
}
