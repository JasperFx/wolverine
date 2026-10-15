using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

public interface IEndpointPolicy : IWolverinePolicy
{
    void Apply(Endpoint endpoint, IWolverineRuntime runtime);
}

/// <summary>
/// GH-5051. An endpoint policy that enforces the application's <see cref="DurabilityMode"/>. The durability mode
/// wins over an endpoint's own configuration, so <see cref="Endpoint.Compile"/> applies these AFTER the
/// endpoint's delayed configuration -- <c>UseDurableOutbox()</c>, <c>SendInline()</c>, <c>ProcessInline()</c> and
/// friends -- rather than before it with every other endpoint policy
/// </summary>
internal interface IDurabilityModeEndpointPolicy : IEndpointPolicy;

internal static class DurabilityModeEndpointPolicyExtensions
{
    /// <summary>
    /// GH-5051. Was the endpoint's current mode asked for explicitly by the endpoint's own listening or
    /// sending configuration, as opposed to a transport default or an endpoint policy?
    /// </summary>
    internal static bool ModeWasExplicitlyRequested(this Endpoint endpoint)
    {
        return endpoint.ListenerRequestedMode == endpoint.Mode || endpoint.SubscriberRequestedMode == endpoint.Mode;
    }

    internal static void WarnAboutOverriddenEndpointMode(this IWolverineRuntime runtime, Endpoint endpoint,
        EndpointMode requested, DurabilityMode durabilityMode)
    {
        runtime.Logger.LogWarning(
            "Endpoint {Uri} was explicitly configured as {Requested}, but DurabilityMode.{DurabilityMode} decides the mode of every endpoint, "
            + "so Wolverine is running it as {Actual} instead.",
            endpoint.Uri, requested, durabilityMode, endpoint.Mode);
    }
}

internal class ServerlessEndpointsMustBeInlinePolicy : IDurabilityModeEndpointPolicy
{
    private readonly List<Uri> _downgradedDurableEndpoints = [];

    internal IReadOnlyList<Uri> DowngradedDurableEndpoints
    {
        get
        {
            lock (_downgradedDurableEndpoints)
            {
                return _downgradedDurableEndpoints.ToArray();
            }
        }
    }

    /// <summary>
    /// GH-5016. Plain Serverless silently dropped the outbox of every UseDurableOutbox() endpoint. Report all
    /// of them in one warning -- a warning, not an error, so existing apps keep starting
    /// </summary>
    internal void WarnAboutDowngradedDurableEndpoints(ILogger logger)
    {
        var downgraded = DowngradedDurableEndpoints;
        if (downgraded.Count == 0) return;

        logger.LogWarning(
            "Serverless mode requires every endpoint to be Inline, so Wolverine has downgraded these durable endpoints to Inline: {Endpoints}. "
            + "The transactional outbox guarantee no longer applies to messages sent to them. "
            + "Use DurabilityMode.{Mode} to keep the outbox in a serverless deployment.",
            string.Join(", ", downgraded), nameof(DurabilityMode.ServerlessWithOutbox));
    }

    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        try
        {
            // GH-5030. Persistence extensions can register system/control endpoints whose transports pin them to
            // a non-Inline mode. Preserve their transport-defined mode; only application endpoints must support
            // Inline in Serverless mode. Checked first, so an endpoint left alone is never reported as downgraded
            if (endpoint.Role == EndpointRole.System && !endpoint.SupportsMode(EndpointMode.Inline))
            {
                return;
            }

            // GH-5051. Serverless removes the local transport at startup, but this policy is registered in the
            // WolverineRuntime constructor now, so resource setup can compile a local queue before that happens.
            // A local queue can never be Inline, so leave it alone rather than fail resource setup
            if (endpoint is Transports.Local.LocalQueue)
            {
                return;
            }

            if (endpoint.Mode == EndpointMode.Durable)
            {
                lock (_downgradedDurableEndpoints)
                {
                    if (!_downgradedDurableEndpoints.Contains(endpoint.Uri)) _downgradedDurableEndpoints.Add(endpoint.Uri);
                }
            }

            // GH-3708. Coercing NativeAck to Inline is correct for Serverless -- there is no long-running process
            // to hold an execution block -- but it silently drops the endpoint's partitioning and parallelism, and
            // partitioned processing is a GUARANTEE the user asked for. Say so rather than degrading in silence.
            if (endpoint.Mode == EndpointMode.NativeAck)
            {
                runtime.Logger.LogWarning(
                    "Endpoint {Uri} was configured with ProcessInParallelWithNativeAcks(), but Serverless mode requires every endpoint to be Inline. "
                    + "Wolverine has downgraded it, which means its parallelism and any PartitionProcessingByGroupId() grouping no longer apply. "
                    + "Messages are still settled natively; they are simply processed one at a time on the transport's listening callback.",
                    endpoint.Uri);
            }

            // GH-5051. Durable and NativeAck endpoints are reported above, so only warn here about the other
            // explicit choices this policy overrides, like a per-endpoint BufferedInMemory()
            var requested = endpoint.Mode;
            var warnAboutOverride = requested is not (EndpointMode.Inline or EndpointMode.Durable or EndpointMode.NativeAck)
                                    && endpoint.ModeWasExplicitlyRequested();

            endpoint.Mode = EndpointMode.Inline;

            if (warnAboutOverride)
            {
                runtime.WarnAboutOverriddenEndpointMode(endpoint, requested, DurabilityMode.Serverless);
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("All endpoints must be Inline when running in Serverless mode", e);
        }
    }
}

/// <summary>
/// GH-5016. The ServerlessWithOutbox counterpart of <see cref="ServerlessEndpointsMustBeInlinePolicy"/>: every
/// endpoint that can be durable is made durable, so the transactional outbox is kept for every sending
/// endpoint. Endpoints whose transport cannot be durable fall back to Inline, and an endpoint locked to
/// some other mode is left alone
/// </summary>
internal class ServerlessWithOutboxEndpointsPolicy : IDurabilityModeEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        var requested = endpoint.Mode;
        var explicitlyRequested = endpoint.ModeWasExplicitlyRequested();

        if (endpoint.SupportsMode(EndpointMode.Durable))
        {
            endpoint.Mode = EndpointMode.Durable;
        }
        else if (endpoint.SupportsMode(EndpointMode.Inline))
        {
            endpoint.Mode = EndpointMode.Inline;
        }

        // Otherwise, like the RavenDb control endpoint that only supports BufferedInMemory, the endpoint keeps
        // the one mode it can run in rather than failing the application at startup

        // GH-5051. A per-endpoint SendInline() used to beat this policy and quietly skip the outbox the
        // application chose this mode for. The mode wins now, but say so
        if (explicitlyRequested && endpoint.Mode != requested)
        {
            runtime.WarnAboutOverriddenEndpointMode(endpoint, requested, DurabilityMode.ServerlessWithOutbox);
        }
    }
}

public class LambdaEndpointPolicy<T> : IEndpointPolicy where T : Endpoint
{
    private readonly Action<T, IWolverineRuntime> _configure;

    public LambdaEndpointPolicy(Action<T, IWolverineRuntime> configure)
    {
        _configure = configure;
    }

    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        if (endpoint is T e)
        {
            _configure(e, runtime);
        }
    }
}
