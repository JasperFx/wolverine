using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Schema;

namespace SqlServerTests;

// GH-4316: the 5-second recovery poll and the per-listener recovery load ask for
// `owner_id = 0` — exactly the value the GH-3971 owner index excludes — and the expired-handled
// cleanup filters on `status = 'Handled' and keep_until <= now` with no index at all, so all of
// them were full scans of an inbox dominated by retained Handled rows. The envelope tables now
// provision filtered indexes for the recoverable and expired-handled slices. These tests prove the
// indexes are created AND that their compound filter predicates round-trip through sys.indexes
// (SqlServer stores e.g. `([status]='Incoming' AND [owner_id]=(0))`, which must canonicalize back
// to the configured predicate or Weasel drops+recreates the index on every startup).
[Collection("sqlserver")]
public class EnvelopeTables_recovery_index_creation : IAsyncLifetime
{
    private SqlConnection theConnection = null!;

    public async ValueTask InitializeAsync()
    {
        theConnection = new SqlConnection(Servers.SqlServerConnectionString);
        await theConnection.OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theConnection.DisposeAsync();
    }

    [Fact]
    public async Task incoming_recovery_indexes_are_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_incoming", ct: TestContext.Current.CancellationToken);

        var table = new IncomingEnvelopeTable(new DurabilitySettings(), "env_idx_incoming");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));
        table.Indexes.ShouldContain(x => x.Name.Contains("keep_until"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        // Re-reading the just-created schema must report NO difference. If a filtered-index
        // predicate did not round-trip, this would come back as Update and thrash on every startup.
        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task outgoing_recovery_index_is_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_outgoing", ct: TestContext.Current.CancellationToken);

        var table = new OutgoingEnvelopeTable(new DurabilitySettings(), "env_idx_outgoing");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }
}
