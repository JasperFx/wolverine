namespace Wolverine.Transports;

/// <summary>
/// The connection state of an endpoint's underlying transport channel/agent, as reported by transports that
/// have a notion of an open connection (e.g. RabbitMQ channels). Surfaced on <see cref="Wolverine.Configuration.EndpointHealthSnapshot"/>
/// so external monitors can distinguish a healthy listener from one whose transport channel has died but whose
/// orchestration <c>ListeningStatus</c> still reports <c>Accepting</c>.
/// </summary>
public enum TransportConnectionState
{
    /// <summary>
    /// The transport does not expose any connection state, or the endpoint is not in a state where a connection
    /// is meaningful (e.g. in-memory local queues). This is the default.
    /// </summary>
    Unknown,

    /// <summary>
    /// The underlying transport channel/connection is open and usable.
    /// </summary>
    Connected,

    /// <summary>
    /// The underlying transport channel/connection is down. For a listener this means it is not actually
    /// consuming even if its <c>ListeningStatus</c> still reports <c>Accepting</c>.
    /// </summary>
    Disconnected,

    /// <summary>
    /// The transport is actively attempting to re-establish a connection.
    /// </summary>
    Reconnecting
}
