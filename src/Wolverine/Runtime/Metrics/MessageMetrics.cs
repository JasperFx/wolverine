using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Message type to send remotely
/// </summary>
public record MessageMetrics(MessageHandlingMetrics[] Handled);