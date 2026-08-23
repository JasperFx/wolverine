using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

public interface IEndpointPolicy : IWolverinePolicy
{
    void Apply(Endpoint endpoint, IWolverineRuntime runtime);
}

internal class ServerlessEndpointsMustBeInlinePolicy : IEndpointPolicy
{
    public void Apply(Endpoint endpoint, IWolverineRuntime runtime)
    {
        try
        {
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