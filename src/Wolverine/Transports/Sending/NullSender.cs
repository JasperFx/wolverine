using System;
using System.Threading.Tasks;

namespace Wolverine.Transports.Sending;

internal class NullSender : ISender
{
    public NullSender(Uri destination)
    {
        Destination = destination;
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public ValueTask SendAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }
}
