using Wolverine.Transports.Sending;

namespace Wolverine.Oracle.Transport;

internal interface IOracleQueueSender : ISender
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}
