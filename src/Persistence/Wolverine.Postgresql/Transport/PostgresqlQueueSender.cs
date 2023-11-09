using Wolverine.Configuration;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport;

internal class PostgresqlQueueSender : ISender
{
    private readonly PostgresqlQueue _queue;

    public PostgresqlQueueSender(PostgresqlQueue queue)
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