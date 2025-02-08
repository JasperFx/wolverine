using JasperFx.Resources;
using Wolverine.Configuration;
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

    Endpoint? ReplyEndpoint();

    Endpoint GetOrCreateEndpoint(Uri uri);
    Endpoint? TryGetEndpoint(Uri uri);

    public IEnumerable<Endpoint> Endpoints();

    ValueTask InitializeAsync(IWolverineRuntime runtime);

    bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource);
}