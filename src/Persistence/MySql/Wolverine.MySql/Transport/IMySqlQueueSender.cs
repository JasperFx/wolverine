using Wolverine.Transports.Sending;

namespace Wolverine.MySql.Transport;

internal interface IMySqlQueueSender : ISender
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}
