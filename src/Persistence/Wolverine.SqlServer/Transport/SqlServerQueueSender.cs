using Wolverine.Configuration;
using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport;

internal class SqlServerQueueSender : ISender
{
    private readonly SqlServerQueue _queue;

    public SqlServerQueueSender(SqlServerQueue queue)
    {
        _queue = queue;
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination => _queue.Uri;
    public async Task<bool> PingAsync()
    {
        try
        {
            await _queue.CheckAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        if (_queue.Mode == EndpointMode.Durable && envelope.WasPersistedInOutbox)
        {
            if (envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
            {
                await _queue.MoveFromOutgoingToScheduledAsync(envelope, CancellationToken.None);
            }
            else
            {
                await _queue.MoveFromOutgoingToQueueAsync(envelope, CancellationToken.None);
            }
        }
        else
        {
            await _queue.SendAsync(envelope, CancellationToken.None);
        }
    }
}