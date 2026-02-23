using Wolverine.Transports.Sending;

namespace Wolverine.MySql.Transport;

internal interface IMySqlQueueSender : ISenderWithScheduledCancellation
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}
