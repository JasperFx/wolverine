using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Transports;

namespace OracleTests;

// Oracle mirror of the regression for https://github.com/JasperFx/wolverine/issues/2823.
// The Oracle inbox previously did UPDATE-only on retry, which silently no-op'd when
// no row existed (ProcessInline retry #1), and never matched the same column shape
// as ScheduleExecutionAsync. RescheduleExistingEnvelopeForRetryAsync is now an upsert
// aligned with MessageDatabase<T>.
[Collection("oracle")]
public class Bug_2823_reschedule_existing_envelope_for_retry_oracle
{
    [Fact]
    public async Task two_consecutive_reschedules_do_not_throw_duplicate()
    {
        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            SchemaName = "WOLVERINE",
            CommandQueuesEnabled = true,
            Role = MessageStoreRole.Main
        };
        var store = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance);

        await store.Admin.MigrateAsync();
        await store.Admin.ClearAllAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Message = new Message1();
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(1);
        envelope.Attempts = 1;

        // Retry #1: no pre-existing row — UPDATE affects 0 rows, INSERT fallback runs.
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        // Retry #2: row from #1 already exists — UPDATE succeeds.
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.Attempts = 2;
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var rows = (await store.Admin.AllIncomingAsync())
            .Where(x => x.Id == envelope.Id)
            .ToList();

        rows.Count.ShouldBe(1);

        var persisted = rows[0];
        persisted.Attempts.ShouldBe(2);
        persisted.ScheduledTime!.Value.ShouldBe(envelope.ScheduledTime.Value, TimeSpan.FromSeconds(2));
        persisted.OwnerId.ShouldBe(TransportConstants.AnyNode);
    }
}
