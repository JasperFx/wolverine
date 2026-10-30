using Dekaf.Errors;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// GH-3454 (split from GH-3237): derives a degrade-only <see cref="TransportConnectionState"/> from the
/// exceptions the consume loop observes. It may only ever move state toward trouble
/// (Reconnecting/Disconnected), never synthesize Connected — the resting state for a healthy consumer is
/// Unknown, and a successfully consumed record clears back to it. Liveness is answered separately by
/// <see cref="BackgroundReceiveLoop"/> health (GH-3236).
/// </summary>
public class KafkaConnectionStateTracker : IReportConnectionState
{
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Unknown;

    public TransportConnectionState ConnectionState => _connectionState;

    public void ApplyException(Exception exception)
    {
        switch (exception)
        {
            // Bootstrap servers can't be resolved at all — nothing to connect to
            case BootstrapResolutionException:
                _connectionState = TransportConnectionState.Disconnected;
                return;

            // Broker-level transport trouble the client retries under the covers, or an initialization that
            // ran out of time. Never allowed to downgrade Disconnected — only a successful consume clears it
            case KafkaTimeoutException:
            case TimeoutException:
            case OperationCanceledException:
            case KafkaException { IsRetriable: true }:
                if (_connectionState != TransportConnectionState.Disconnected)
                {
                    _connectionState = TransportConnectionState.Reconnecting;
                }

                return;
        }

        // Anything else (deserialization trouble, authorization on one topic, ...) is not evidence
        // about the connection, so leave the state alone
    }

    public void MarkSuccessfulConsume()
    {
        if (_connectionState != TransportConnectionState.Unknown)
        {
            // Records are flowing again, so any previously derived trouble state is stale
            _connectionState = TransportConnectionState.Unknown;
        }
    }
}
