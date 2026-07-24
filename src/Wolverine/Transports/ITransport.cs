using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Transports;

/// <summary>
/// Transports that need to utilize the IWolverineRuntime to adjust
/// message storage or other aspects of the Wolverine runtime just before
/// configuring message storage should implement this interface
/// </summary>
public interface ITransportConfiguresRuntime
{
    ValueTask ConfigureAsync(IWolverineRuntime runtime);
}

/// <summary>
/// Used for transports to register agent families
/// </summary>
public interface IAgentFamilySource
{
    IEnumerable<IAgentFamily> BuildAgentFamilySources(IWolverineRuntime runtime);
}

public interface ITransport
{
    public string Protocol { get; }

    /// <summary>
    ///     Strictly a diagnostic name for this transport type
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Human- and AI-readable description of this transport, folded into routing explanations
    ///     for conventional (broker) routing. The default reports the name and scheme; individual
    ///     transports should override to explain what the broker is and how conventional routing
    ///     maps message types onto it.
    /// </summary>
    string Describe() => $"{Name} broker (scheme '{Protocol}')";

    /// <summary>
    /// A sanitized, credential-free, human-readable summary of what this broker is pointing to — the connection
    /// target (host + port, virtual host, namespace, region, bootstrap servers, …) for diagnostic consumers such as
    /// CritterWatch's messaging topology. MUST be built from parsed connection components, NEVER from a raw connection
    /// string: no usernames, passwords, SAS keys, or secrets of any kind. Returns null when the transport has no
    /// meaningful connection target (e.g. the in-memory/local transport) or it cannot be determined safely.
    /// </summary>
    string? DescribeEndpoint() => null;

    Endpoint? ReplyEndpoint();

    Endpoint GetOrCreateEndpoint(Uri uri);
    Endpoint? TryGetEndpoint(Uri uri);

    public IEnumerable<Endpoint> Endpoints();

    ValueTask InitializeAsync(IWolverineRuntime runtime);

    /// <summary>
    /// Build out this transport's endpoint topology (compile configured endpoints, derive system
    /// endpoints such as dead letter, response, and retry queues) without connecting to the
    /// underlying broker or database. Resource discovery uses this instead of InitializeAsync so
    /// that "resources setup" can enumerate a transport's stateful resource for a target that an
    /// IResourceCreator in the same pass is about to create. Defaults to the full InitializeAsync
    /// for transports that make no distinction.
    /// </summary>
    ValueTask InitializeEndpointsAsync(IWolverineRuntime runtime) => InitializeAsync(runtime);

    bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource);

    bool TryBuildBrokerUsage(out BrokerDescription description);

    /// <summary>
    /// Build a transport-specific health check. Returns null if this transport
    /// does not support health checking (e.g., local transport).
    /// </summary>
    WolverineTransportHealthCheck? BuildHealthCheck(IWolverineRuntime runtime) => null;
}