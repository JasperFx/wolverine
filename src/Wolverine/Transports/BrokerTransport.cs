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
    protected BrokerTransport(string protocol, string name, string[] tags) : base(protocol, name, tags)
    {
    }

    public abstract Uri ResourceUri { get; }

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

        await InitializeEndpointsAsync(runtime);

        // Whatever actually went wrong, kept so the exception thrown after the last attempt can carry
        // it. Without this the only thing a caller ever sees is "Unable to initialize the Broker asb
        // in time", and the cause survives nowhere but a log line inside a two-minute retry loop.
        // GH-3786 was a flat 400 Bad Request on an illegal entity name -- never going to succeed on
        // attempt 20 either -- and it read as a timeout for four months.
        Exception? lastFailure = null;

        // GH-4116. Bounded by a WALL CLOCK, not only by an attempt count. "Twenty attempts" bounds tries,
        // not time, and one try costs whatever the broker client's own request timeout is -- 60s for
        // librdkafka -- so an unreachable or degraded broker turned host startup into a ~21 minute hang
        // with no way out. Measured against a closed port: >20 minutes. Longer than any sane orchestrator
        // start probe, and longer than our own 20-minute CI job cap, which is what turns a readable startup
        // failure into an unreadable job cancellation with its logs discarded. See also GH-4100.
        var budget = runtime.Options.BrokerInitializationTimeout;
        var deadline = DateTimeOffset.UtcNow + budget;

        for (int i = 0; i < 20; i++)
        {
            try
            {
                await startupAsync(runtime);
                return;
            }
            catch (Exception e)
            {
                lastFailure = e;
                runtime.Logger.LogError(e, "Error trying to start message broker {Broker} on Attempt {Attempt} of 20", Protocol, i + 1);

                // The attempt just above cannot be interrupted -- broker client SDKs do not take a
                // CancellationToken on their provisioning calls -- so the budget is checked between
                // attempts and the real worst case is the budget plus one attempt.
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    runtime.Logger.LogError(
                        "Giving up on starting message broker {Broker} after {Attempts} attempt(s); the {Budget} budget in WolverineOptions.BrokerInitializationTimeout has elapsed",
                        Protocol, i + 1, budget);
                    break;
                }

                if (i < 19)
                {
                    runtime.Logger.LogInformation("Will retry to start broker {Broker} in 5 seconds", Protocol);

                    // Also GH-4116: the delay used to ignore cancellation entirely, so a host being shut
                    // down -- or a Ctrl-C -- could not break out of this loop either.
                    try
                    {
                        await Task.Delay(5.Seconds(), runtime.Cancellation);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        throw new BrokerInitializationException(this, lastFailure);

    }

    // Nothing here may touch the broker or database: resource discovery runs this against targets
    // that may not exist yet. BrokerResource re-runs ConnectAsync at the start of every operation,
    // so deferring the connection loses nothing.
    public ValueTask InitializeEndpointsAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in explicitEndpoints())
        {
            endpoint.Compile(runtime);
        }

        // A transport that builds system endpoints needing the endpoint policies applied to them --
        // a dead letter queue whose type must match what the runtime declares later, say -- has to
        // compile them here itself. BrokerResource.Setup() declares whatever it finds in Endpoints()
        // without compiling anything, so an uncompiled system endpoint gets declared with pre-policy
        // settings and then redeclared with the policy applied at start up. See GH-3871.
        tryBuildSystemEndpoints(runtime);

        return ValueTask.CompletedTask;
    }

    private async ValueTask startupAsync(IWolverineRuntime runtime)
    {
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

public class BrokerInitializationException : Exception
{
    public BrokerInitializationException(IBrokerTransport transport) : this(transport, null)
    {

    }

    /// <param name="innerException">
    /// The failure from the LAST startup attempt. Always pass it when there was one: the message here
    /// says only that the broker did not come up in time, which reads as a timing problem even when
    /// the cause was a flat rejection that all twenty attempts were guaranteed to reproduce.
    /// </param>
    public BrokerInitializationException(IBrokerTransport transport, Exception? innerException)
        : base($"Unable to initialize the Broker {transport.Protocol} in time", innerException)
    {

    }
}