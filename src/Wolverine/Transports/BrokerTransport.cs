using JasperFx.Core;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports;

/// <summary>
///     Abstract base class suitable for brokered messaging infrastructure
/// </summary>
/// <typeparam name="TEndpoint"></typeparam>
public abstract class BrokerTransport<TEndpoint> : TransportBase<TEndpoint>, IBrokerTransport
    where TEndpoint : Endpoint, IBrokerEndpoint
{
    protected BrokerTransport(string protocol, string name) : base(protocol, name)
    {
    }
    
    
    /// <summary>
    /// In the case of using multi-tenancy support at the transport level (generally, a separate message broker or namespace or whatever per tenant),
    /// this governs the behavior of message sending in regards to a tenant id. Default behavior is to fall back to the default
    /// connection in the case of no tenant id
    /// </summary>
    public TenantedIdBehavior TenantedIdBehavior { get; set; } = TenantedIdBehavior.FallbackToDefault;


    /// <summary>
    ///     Used as a separator for prefixed identifiers
    /// </summary>
    protected string IdentifierDelimiter { get; set; } = "-";

    /// <summary>
    ///     Optional prefix to append to all messaging object identifiers to make them unique when multiple developers
    ///     need to develop against a common message broker. I.e., sigh, you have to be using a cloud only tool.
    /// </summary>
    public string? IdentifierPrefix { get; set; }

    public string MaybeCorrectName(string identifier)
    {
        if (IdentifierPrefix.IsEmpty())
        {
            return SanitizeIdentifier(identifier);
        }

        return SanitizeIdentifier($"{IdentifierPrefix}{IdentifierDelimiter}{identifier}");
    }

    /// <summary>
    ///     Use to sanitize names for illegal characters
    /// </summary>
    /// <param name="identifier"></param>
    /// <returns></returns>
    public virtual string SanitizeIdentifier(string identifier)
    {
        return identifier;
    }

    /// <summary>
    ///     Should Wolverine attempt to auto-provision all declared or discovered objects?
    /// </summary>
    public bool AutoProvision { get; set; }

    /// <summary>
    ///     Should Wolverine attempt to purge all messages out of existing or discovered queues
    ///     on application start up? This can be useful for testing, and occasionally for ephemeral
    ///     messages
    /// </summary>
    public bool AutoPurgeAllQueues { get; set; }

    public sealed override bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = new BrokerResource(this, runtime);
        return true;
    }

    public abstract ValueTask ConnectAsync(IWolverineRuntime runtime);
    public abstract IEnumerable<PropertyColumn> DiagnosticColumns();

    public sealed override async ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        runtime.Logger.LogInformation("Initializing the Wolverine {TransportName}", GetType().Name);

        foreach (var endpoint in explicitEndpoints())
        {
            endpoint.Compile(runtime);
        }

        tryBuildSystemEndpoints(runtime);

        await ConnectAsync(runtime);

        foreach (var endpoint in endpoints())
        {
            endpoint.Compile(runtime);
            await endpoint.InitializeAsync(runtime.Logger);
        }
    }

    /// <summary>
    /// This should be overridden in transports that infer dead letter queues from
    /// the main endpoints so that dead letter queue configuration is applied
    /// before trying to derive DLQ endpoints
    /// </summary>
    /// <returns></returns>
    protected virtual IEnumerable<Endpoint> explicitEndpoints()
    {
        return endpoints();
    }

    /// <summary>
    ///     Template method hook to build dedicated response endpoints
    ///     or dead letter queue endpoints for the transport
    /// </summary>
    /// <param name="runtime"></param>
    protected virtual void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
    }
}