using Wolverine.Transports.Sending;

namespace Wolverine.Oracle.Transport;

internal interface IOracleQueueSender : ISenderWithScheduledCancellation
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken);
}
