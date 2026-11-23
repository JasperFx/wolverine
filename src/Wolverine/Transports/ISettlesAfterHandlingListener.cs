namespace Wolverine.Transports;

/// <summary>
/// GH-5926. A broker listener whose deliveries a buffered receiver settles once each message reaches a terminal (the
/// handler succeeded, the message was discarded or dead lettered), and not when it arrives. A buffered receiver
/// normally settles a delivery as soon as it has queued it, which suits a broker that holds unsettled deliveries
/// against a prefetch window. A listener implements this when settling late costs it nothing and settling early
/// would let the broker forget a message that never ran: Kafka's committed offset, for one, moves past everything a
/// buffered listener has merely queued, so a node that stops with work in its buffer used to lose it.
/// <para>
/// A delivery that never reaches a terminal on this node, because the node stopped first, is never settled, so the
/// broker delivers it again.
/// </para>
/// </summary>
internal interface ISettlesAfterHandlingListener : IListener;
