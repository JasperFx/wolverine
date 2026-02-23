using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Envelope message type used to transmit an array of <see cref="MessageHandlingMetrics"/>
/// snapshots, typically for remote publishing or aggregation across nodes.
/// </summary>
/// <param name="Handled">The array of metrics snapshots to transmit.</param>
public record MessageMetrics(MessageHandlingMetrics[] Handled);
