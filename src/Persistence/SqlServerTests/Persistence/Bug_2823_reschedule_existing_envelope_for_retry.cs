using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;

namespace SqlServerTests.Persistence;

// Regression for https://github.com/JasperFx/wolverine/issues/2823.
// With ProcessInline + ScheduleRetry(RetryCount > 1), the second retry called
// RescheduleExistingEnvelopeForRetryAsync against a row that had already been
// inserted by the first retry, blowing up on the primary key (id, received_at)
// with DuplicateIncomingEnvelopeException. RescheduleExistingEnvelopeForRetryAsync
// now upserts: UPDATE first, INSERT fallback if no row exists.
public class Bug_2823_reschedule_existing_envelope_for_retry : SqlServerContext
{
    [Fact]
    public async Task two_consecutive_reschedules_do_not_throw_duplicate()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Message = new Message1();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(1);
        envelope.Attempts = 1;

        // Retry #1: no pre-existing row — UPDATE affects 0 rows, INSERT fallback runs.
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        // Retry #2: row from #1 already exists — UPDATE succeeds, no INSERT.
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.Attempts = 2;
        await thePersistence.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var rows = (await thePersistence.As<SqlServerMessageStore>().Admin.AllIncomingAsync())
            .Where(x => x.Id == envelope.Id)
            .ToList();

        rows.Count.ShouldBe(1);

        var persisted = rows[0];
        persisted.Attempts.ShouldBe(2);
        persisted.ScheduledTime!.Value.ShouldBe(envelope.ScheduledTime.Value, TimeSpan.FromSeconds(2));
        persisted.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }
}
