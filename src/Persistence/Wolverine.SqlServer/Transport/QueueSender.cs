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
        // TODO -- switch a little bit based on the durability
        await _queue.SendAsync(envelope, CancellationToken.None);
    }
}