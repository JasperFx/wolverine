using Confluent.Kafka;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// GH-3454 (split from GH-3237): librdkafka hides connection/group state behind <c>Consume()</c>, so this
/// tracker derives a degrade-only <see cref="TransportConnectionState"/> from the consumer's error callback.
/// It may only ever move state toward trouble (Reconnecting/Disconnected), never synthesize Connected — the
/// resting state for a healthy consumer is Unknown, and a successfully consumed record clears back to it.
/// Liveness is answered separately by <see cref="BackgroundReceiveLoop"/> health (GH-3236).
/// </summary>
public class KafkaConnectionStateTracker : IReportConnectionState
{
    private volatile TransportConnectionState _connectionState = TransportConnectionState.Unknown;

    public TransportConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// True when the error handler could not be registered because user configuration already claimed it
    /// through ConfigureConsumerBuilders (Confluent's builder throws on double registration). The state
    /// then rests at Unknown permanently rather than breaking existing configurations.
    /// </summary>
    public bool ErrorHandlerSuppressed { get; internal set; }

    public void ApplyError(Error error)
    {
        if (error.IsFatal || error.Code == ErrorCode.Local_AllBrokersDown)
        {
            _connectionState = TransportConnectionState.Disconnected;
            return;
        }

        switch (error.Code)
        {
            // Broker-level transport trouble that librdkafka retries under the covers. Never allowed to
            // downgrade Disconnected — the aggregate all-brokers-down signal outranks a per-broker error,
            // and only a successful consume clears it
            case ErrorCode.Local_Transport:
            case ErrorCode.Local_TimedOut:
            case ErrorCode.Local_Resolve:
                if (_connectionState != TransportConnectionState.Disconnected)
                {
                    _connectionState = TransportConnectionState.Reconnecting;
                }

                break;
        }
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
