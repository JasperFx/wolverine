using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

public interface IEndpointPolicy : IWolverinePolicy
{
    void Apply(Endpoint endpoint, IWolverineRuntime runtime);
}

internal class ServerlessEndpointsMustBeInlinePolicy : IEndpointPolicy
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

            endpoint.Mode = EndpointMode.Inline;
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
internal class ServerlessWithOutboxEndpointsPolicy : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
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
