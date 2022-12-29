using System;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Persistence.Durability;

public interface IDurabilityAgent : IHostedService, IAsyncDisposable
{
    void EnqueueLocally(Envelope envelope);
    void RescheduleIncomingRecovery();
    void RescheduleOutgoingRecovery();
}