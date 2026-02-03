using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport;

internal interface ISqlServerQueueSender : ISender
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}
