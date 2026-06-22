using Microsoft.Extensions.Hosting;
using Wolverine.Pulsar.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Pulsar;

public static class PulsarReplayExtensions
{
    /// <summary>
    /// Run a bounded, one-shot replay of a Pulsar topic's history back through the normal Wolverine
    /// handler pipeline (GH-3184). Uses a throwaway, non-durable <c>Reader</c> cursor and never touches
    /// any live durable subscription, so steady-state consumption is undisturbed. Replayed messages are
    /// re-handled, so handlers should be idempotent.
    /// </summary>
    /// <param name="host">A running Wolverine host configured with the Pulsar transport.</param>
    /// <param name="request">The bounded window to replay.</param>
    /// <param name="token"></param>
    public static async Task<PulsarReplayResult> ReplayPulsarTopicAsync(this IHost host, PulsarReplayRequest request,
        CancellationToken token = default)
    {
        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<PulsarTransport>();

        return await new PulsarReplay(transport, runtime).ExecuteAsync(request, token);
    }
}
