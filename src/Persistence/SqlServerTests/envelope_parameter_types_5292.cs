using System.Data;
using System.Text.RegularExpressions;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace SqlServerTests;

/// <summary>
/// GH-5292. The inbox commands built per envelope -- recovery reassignment, delete, dead letter move and the
/// batched mark-as-handled -- bound the envelope id through Weasel's generic builder, which has no mapping for
/// Guid and typed it as sql_variant. Comparing that with the uniqueidentifier id column converted the column on
/// every row, so each single-row statement scanned the incoming table instead of seeking on its primary key.
/// The recovery page loads bound the address as nvarchar against a varchar column, which converted the column
/// the same way and scanned instead of seeking on the filtered address index.
/// </summary>
public class envelope_parameter_types_5292 : IAsyncLifetime
{
    private const string SchemaName = "gh_5292";

    private IHost theHost = null!;
    private SqlServerMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        theStore = theHost.GetRuntime().Storage.As<SqlServerMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void the_command_builder_lets_sql_client_type_what_the_generic_mapping_gets_wrong()
    {
        var when = new DateTime(2026, 10, 30, 13, 45, 12, DateTimeKind.Utc);

        var builder = theStore.ToCommandBuilder();
        builder.AppendParameter(Guid.NewGuid());
        builder.AppendParameter(3);
        builder.AppendParameter("stub://one");
        builder.AddNamedParameter("named_id", (object)Guid.NewGuid());
        builder.AddNamedParameter("named_time", (object)when);
        builder.AppendParameter((object)DBNull.Value, DbType.Guid);

        var parameters = builder.Compile().Parameters.Cast<SqlParameter>().ToArray();

        parameters[0].SqlDbType.ShouldBe(SqlDbType.UniqueIdentifier);
        parameters[1].SqlDbType.ShouldBe(SqlDbType.Int);
        parameters[2].SqlDbType.ShouldBe(SqlDbType.NVarChar);
        parameters[3].SqlDbType.ShouldBe(SqlDbType.UniqueIdentifier);

        // Not date, which would drop the time of day
        parameters[4].SqlDbType.ShouldBe(SqlDbType.DateTime);

        // A null has nothing to infer from, so it keeps the type it was given
        parameters[5].SqlDbType.ShouldBe(SqlDbType.UniqueIdentifier);
    }

    [Fact]
    public async Task no_envelope_command_converts_an_indexed_column()
    {
        var startedAt = await serverTimeAsync();

        var envelopes = Enumerable.Range(0, 8).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = "stub://one".ToUri();
            envelope.Status = EnvelopeStatus.Incoming;
            envelope.OwnerId = TransportConstants.AnyNode;
            return envelope;
        }).ToArray();

        await theStore.Inbox.StoreIncomingAsync(envelopes);

        // The recovery reads, which filter on the address alone
        (await theStore.LoadPageOfGloballyOwnedIncomingAsync("stub://one".ToUri(), 100)).Count.ShouldBe(8);
        await theStore.Outbox.LoadOutgoingAsync("stub://one".ToUri());

        await theStore.ReassignIncomingAsync(theStore.Durability.AssignedNodeNumber, envelopes);
        await theStore.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelopes[..3]);
        await theStore.Inbox.DeleteIncomingEnvelopeAsync(envelopes[3]);
        await theStore.Inbox.MoveToDeadLetterStorageAsync(envelopes[4], new DivideByZeroException());

        var statements = await statementsSinceAsync(startedAt);

        // Every one of these commands has to have been found, or the assertions below prove nothing
        statements.ShouldContain(x => x.Text.Contains("received_at = @address"));
        statements.ShouldContain(x => x.Text.Contains("destination = @destination"));
        statements.ShouldContain(x => x.Text.Contains("set owner_id = "));
        statements.ShouldContain(x => x.Text.Contains("status = 'Handled'"));
        statements.ShouldContain(x => x.Text.Contains("delete from", StringComparison.OrdinalIgnoreCase));

        // The parameter declarations of an sp_executesql batch are part of its cached text
        statements.ShouldAllBe(x => !x.Text.Contains("sql_variant"));

        // A conversion of a gh_5292 column, rather than of a parameter, is what loses the index seek
        statements.Where(x => ColumnConversion.IsMatch(x.Plan)).Select(x => x.Text).ShouldBeEmpty();
    }

    private static readonly Regex ColumnConversion =
        new(@"CONVERT_IMPLICIT\([a-z_]+(\(\d+\))?,\[[^\]]+\]\.\[gh_5292\]\.", RegexOptions.Compiled);

    private static async Task<DateTime> serverTimeAsync()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        return (DateTime)(await conn.CreateCommand("select sysdatetime()").ExecuteScalarAsync())!;
    }

    private static async Task<(string Text, string Plan)[]> statementsSinceAsync(DateTime startedAt)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand("""
                                     select t.text, p.query_plan
                                     from sys.dm_exec_query_stats qs
                                     cross apply sys.dm_exec_sql_text(qs.sql_handle) t
                                     cross apply sys.dm_exec_text_query_plan(qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset) p
                                     where qs.last_execution_time >= @startedAt
                                       and t.text like '%gh[_]5292.wolverine[_]%'
                                     """);
        // datetime2, so the server clock reading is not shifted by this machine's offset on its way back
        cmd.Parameters.Add(new SqlParameter("startedAt", SqlDbType.DateTime2) { Value = startedAt });

        var statements = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            statements.Add((reader.GetString(0), await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1)));
        }

        return statements.ToArray();
    }
}
