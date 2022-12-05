using System;

namespace Wolverine.Transports;

/// <summary>
///     Back pressure limits for buffered or durable listeners to govern
///     when incoming messages should be paused or restarted
/// </summary>
/// <param name="Maximum"></param>
/// <param name="Restart"></param>
public record BufferingLimits(int Maximum, int Restart);

/// <summary>
///     Exposed to the DurabilityAgent to manage incoming message
///     recovery with back pressure semantics
/// </summary>
internal interface IDurableProcessor
{
    /// <summary>
    ///     Number of currently queued messages
    /// </summary>
    int QueuedCount { get; }

    /// <summary>
    ///     Buffering limits
    /// </summary>
    BufferingLimits Buffering { get; }

    Uri Uri { get; }
    ListeningStatus Status { get; }

    void Enqueue(Envelope envelope);
}