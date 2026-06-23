namespace Wolverine.Transports;

/// <summary>
/// Optional capability implemented by an <see cref="IListener"/>, <see cref="Sending.ISender"/>, or sending/listening
/// agent that can report the state of its underlying transport channel/connection. Transports that have no notion of
/// a connection simply do not implement this, and report <see cref="TransportConnectionState.Unknown"/>.
/// </summary>
public interface IReportConnectionState
{
    /// <summary>
    /// The current connection state of the underlying transport channel/agent.
    /// </summary>
    TransportConnectionState ConnectionState { get; }
}
