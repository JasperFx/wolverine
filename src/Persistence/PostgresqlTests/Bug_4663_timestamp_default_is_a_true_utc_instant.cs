using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace PostgresqlTests;

/// <summary>
/// Regression for https://github.com/JasperFx/wolverine/issues/4663.
///
/// <para>
/// The envelope tables' <c>timestamp</c> column is <c>timestamptz</c> and was declared with the default
/// <c>(now() at time zone 'utc')</c>. That expression returns a <c>timestamp</c> WITHOUT a time zone
/// holding the UTC wall clock, and PostgreSQL coerces it into the <c>timestamptz</c> column using the
/// *inserting session's* zone. So on a server whose <c>timezone</c> is <c>Pacific/Kiritimati</c> (UTC+14)
/// every row was stored fourteen hours in the past, and on <c>America/Santiago</c> (UTC−3) three hours in
/// the future.
/// </para>
///
/// <para>
/// <c>BumpStaleIncomingEnvelopesOperation</c> compares the column against a cutoff computed in .NET as a
/// true UTC instant, so a server ahead of UTC handed back envelopes whose handlers were still running
/// (the reported symptom: a second run 21.8 s into the first), and a server behind UTC never handed back a
/// stranded envelope at all. The official images run UTC, which is why CI never saw it.
/// </para>
///
/// <para>
/// This is the same bug Oracle had until GH-2634, and the same one
/// <c>PostgresqlClaimCheckStore</c> already works around in its own table.
/// </para>
/// </summary>
[Collection("marten")]
public class Bug_4663_timestamp_default_is_a_true_utc_instant : IAsyncLifetime
{
    private const string TheSchema = "tz_skew_4663";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);

                // The column only exists when a stale time is configured.
                opts.Durability.InboxStaleTime = 1.Hours();
                opts.Durability.OutboxStaleTime = 1.Hours();
            }).StartAsync();

        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [InlineData("Pacific/Kiritimati")] // UTC+14 — stored 14h in the past, bumped while still running
    [InlineData("America/Santiago")]   // UTC-3  — stored 3h in the future, never bumped at all
    [InlineData("UTC")]                // the control: this one always passed
    public async Task the_default_stores_a_true_instant_whatever_the_session_zone(string zone)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using (var setTz = conn.CreateCommand())
        {
            setTz.CommandText = $"set time zone '{zone}'";
            await setTz.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var id = Guid.NewGuid();

        // Every column except `timestamp`, so the DEFAULT is what writes it. That is the whole point:
        // nothing in Wolverine ever sets this column explicitly, so the default is its only writer.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText =
                $"""
                 insert into {TheSchema}.{DatabaseConstants.IncomingTable}
                     (id, status, owner_id, body, message_type, received_at)
                 values (@id, 'Incoming', 0, @body, 'tz-probe', 'local://four-six-six-three')
                 """;
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("body", new byte[] { 1, 2, 3 });
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var read = conn.CreateCommand();
        read.CommandText =
            $"""
             select extract(epoch from ("{DatabaseConstants.Timestamp}" - now())) / 3600.0
             from {TheSchema}.{DatabaseConstants.IncomingTable} where id = @id
             """;
        read.Parameters.AddWithValue("id", id);

        var hoursOff = Convert.ToDouble(await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        // Before the fix this read -14.00 for Kiritimati and +3.00 for Santiago.
        Math.Abs(hoursOff).ShouldBeLessThan(0.01);
    }

    [Fact]
    public async Task an_already_provisioned_table_has_its_bad_default_repaired()
    {
        // A corrected DEFAULT is invisible to Weasel's delta -- it compares name and type only unless
        // DetectColumnDrift is on, which Wolverine never sets. So without an explicit repair, every
        // database provisioned before the fix keeps the broken default forever. Put the old default back
        // and prove that coming up against that database corrects it.
        await setDefaultAsync(DatabaseConstants.IncomingTable, "(now() at time zone 'utc')");
        await setDefaultAsync(DatabaseConstants.OutgoingTable, "(now() at time zone 'utc')");

        // PostgreSQL 17 hands this back as `(now() AT TIME ZONE 'utc'::text)`; other versions render the
        // equivalent `timezone('utc'::text, now())`. The repair has to recognise both, and this assertion
        // is deliberately loose so it pins "not repaired yet" rather than one server's spelling.
        (await currentDefaultAsync(DatabaseConstants.IncomingTable)).ShouldNotBe("now()");

        using var next = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Durability.InboxStaleTime = 1.Hours();
                opts.Durability.OutboxStaleTime = 1.Hours();
            }).StartAsync(TestContext.Current.CancellationToken);

        (await currentDefaultAsync(DatabaseConstants.IncomingTable)).ShouldBe("now()");
        (await currentDefaultAsync(DatabaseConstants.OutgoingTable)).ShouldBe("now()");

        await next.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_repair_leaves_a_healthy_default_alone()
    {
        // The repair reads the catalog before it alters, so a healthy database does not take an
        // ACCESS EXCLUSIVE lock on its inbox table on every startup.
        (await currentDefaultAsync(DatabaseConstants.IncomingTable)).ShouldBe("now()");

        using var next = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Durability.InboxStaleTime = 1.Hours();
            }).StartAsync(TestContext.Current.CancellationToken);

        (await currentDefaultAsync(DatabaseConstants.IncomingTable)).ShouldBe("now()");

        await next.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task setDefaultAsync(string tableName, string expression)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"alter table {TheSchema}.{tableName} alter column \"{DatabaseConstants.Timestamp}\" set default {expression}";
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> currentDefaultAsync(string tableName)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"""
             select pg_get_expr(d.adbin, d.adrelid)
             from pg_attrdef d
             join pg_attribute a on a.attrelid = d.adrelid and a.attnum = d.adnum
             where d.adrelid = to_regclass('{TheSchema}.{tableName}')
               and a.attname = '{DatabaseConstants.Timestamp}'
             """;

        var raw = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        raw.ShouldNotBeNull($"No default on {tableName}.{DatabaseConstants.Timestamp}");

        return (string)raw;
    }
}
