using Microsoft.Extensions.Hosting;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;

namespace Wolverine.Kafka;

public static class KafkaReplayExtensions
{
    /// <summary>
    /// Run a bounded, one-shot replay of a Kafka topic's history back through the normal Wolverine
    /// handler pipeline against the default Kafka broker (GH-3147). Uses a throwaway Assign()-based
    /// consumer and never commits to the live consumer group, so steady-state consumption is untouched.
    /// Replayed messages are re-handled, so handlers should be idempotent.
    /// </summary>
    public static Task<KafkaReplayResult> ReplayKafkaTopicAsync(this IHost host, KafkaReplayRequest request,
        CancellationToken token = default)
    {
        return host.ReplayKafkaTopicAsync(request, null, token);
    }

    /// <summary>
    /// Run a bounded, one-shot Kafka replay against a specific named broker. See
    /// <see cref="ReplayKafkaTopicAsync(IHost, KafkaReplayRequest, CancellationToken)"/>.
    /// </summary>
    public static async Task<KafkaReplayResult> ReplayKafkaTopicAsync(this IHost host, KafkaReplayRequest request,
        string? brokerName, CancellationToken token = default)
    {
        var runtime = host.GetRuntime();
        var transport = brokerName == null
            ? runtime.Options.Transports.GetOrCreate<KafkaTransport>()
            : runtime.Options.Transports.GetOrCreate<KafkaTransport>(new BrokerName(brokerName));

        return await new KafkaReplay(transport, runtime).ExecuteAsync(request, token);
    }
}
