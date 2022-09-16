using System;
using System.Collections.Generic;

namespace Wolverine.Logging;

public class NulloMetrics : IMetrics
{
    public void MessageReceived(Envelope? envelope)
    {
    }

    public void MessageExecuted(Envelope? envelope)
    {
    }

    public void LogException(Exception? ex)
    {
    }

    public void CircuitBroken(Uri? destination)
    {
    }

    public void CircuitResumed(Uri? destination)
    {
    }

    public void LogLocalWorkerQueueDepth(int count)
    {
    }

    public void LogPersistedCounts(PersistedCounts counts)
    {
    }

    public void MessagesReceived(IEnumerable<Envelope?> envelopes)
    {
    }

    public void LogLocalSendingQueueDepth(int sendingCount)
    {
    }
}
