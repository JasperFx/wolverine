using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;

namespace SqlServerTests.Persistence;

// GH-4318: FetchCountsAsync answers from sys.dm_db_partition_stats plus the filtered-index row
// counts instead of three full scans. dm_db_partition_stats.row_count is transactionally
// maintained metadata, so the numbers must EXACTLY match what the old group-by scan reported —
// this seeds every status the split has to reconstruct (Incoming, Handled, Scheduled) plus the
// outbox and asserts the arithmetic.
[Collection("sqlserver")]
public class fetch_counts_via_partition_stats : IAsyncLifetime
{
    private IHost _host = null!;
    private IMessageStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("counts_stats");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "counts_stats");
            })
            .StartAsync();

        _store = _host.Services.GetRequiredService<IMessageStore>();
        await _store.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task counts_match_seeded_rows_across_every_status()
    {
        for (var i = 0; i < 5; i++)
        {
            var incoming = ObjectMother.Envelope();
            incoming.Status = EnvelopeStatus.Incoming;
            await _store.Inbox.StoreIncomingAsync(incoming);
        }

        for (var i = 0; i < 3; i++)
        {
            var handled = ObjectMother.Envelope();
            handled.Status = EnvelopeStatus.Incoming;
            await _store.Inbox.StoreIncomingAsync(handled);
            await _store.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);
        }

        for (var i = 0; i < 2; i++)
        {
            // StoreIncomingAsync persists whatever status the envelope carries; the dedicated
            // ScheduleExecutionAsync path is an update against an already-stored row
            var scheduled = ObjectMother.Envelope();
            scheduled.Status = EnvelopeStatus.Scheduled;
            scheduled.OwnerId = 0;
            scheduled.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
            await _store.Inbox.StoreIncomingAsync(scheduled);
        }

        for (var i = 0; i < 4; i++)
        {
            var outgoing = ObjectMother.Envelope();
            await _store.Outbox.StoreOutgoingAsync(outgoing, 0);
        }

        var counts = await _store.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(5);
        counts.Handled.ShouldBe(3);
        counts.Scheduled.ShouldBe(2);
        counts.Outgoing.ShouldBe(4);
        counts.DeadLetter.ShouldBe(0);
    }
}
