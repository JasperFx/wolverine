using Wolverine.Transports.Sending;

namespace Wolverine.SqlServer.Transport;

internal interface ISqlServerQueueSender : ISenderWithScheduledCancellation
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}
