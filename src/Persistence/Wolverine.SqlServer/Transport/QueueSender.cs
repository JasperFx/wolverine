using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport;

internal class QueueSender : ISender
{
    private readonly SqlServerQueue _queue;

    public QueueSender(SqlServerQueue queue)
    {
        _queue = queue;
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination => _queue.Uri;
    public async Task<bool> PingAsync()
    {
        throw new NotImplementedException();
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        throw new NotImplementedException();
    }
}