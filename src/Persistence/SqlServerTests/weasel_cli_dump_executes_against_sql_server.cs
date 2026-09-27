using System.Text.RegularExpressions;
using IntegrationTests;
using JasperFx;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
///     The SQL that Weasel's <c>db-dump</c> writes for Wolverine's SQL Server message store has to run
///     against a real server, as one file, and run a second time without failing.
/// </summary>
/// <remarks>
///     <para>
///     Wolverine's SQL Server storage is the one schema in the Critter Stack that carries stored
///     procedures — five of them, plus the <c>EnvelopeIdList</c> table type they take. T-SQL requires a
///     procedure definition to be the first statement of its batch, so a script that concatenates every
///     object's DDL is only runnable if something separates those batches (weasel#593). That makes this
///     store the sharpest possible test of the generated script, and nothing was testing it: the
///     runtime apply path sends one command per delta, so a procedure landed in a batch of its own by
///     accident and the defect only appeared in a rendered file.
///     </para>
///     <para>
///     The second execution is the other half. A generated script is something people re-run — against
///     the next environment, or after a partial failure — and every statement in it then describes
///     something that already exists.
///     </para>
/// </remarks>
[Collection("sqlserver")]
public class weasel_cli_dump_executes_against_sql_server
{
    private const string Schema = "weasel_dump";

    /// <summary>
    ///     A line whose entire content is <c>GO</c> ends the batch — sqlcmd's rule, spelled out here
    ///     rather than borrowed from <c>Weasel.SqlServer.SqlServerBatchSplitter</c> on purpose.
    ///     Splitting the output with the same code that produced it would make the test agree with
    ///     Weasel about what a batch is instead of agreeing with SQL Server.
    /// </summary>
    private static readonly Regex BatchSeparator =
        new(@"^[ \t]*GO[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    [Fact]
    public async Task db_dump_writes_a_script_that_runs_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"wolverine_db_dump_{Guid.NewGuid():N}.sql");

        try
        {
            var exitCode = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, Schema))
                .RunJasperFxCommands(["db-dump", path]);

            exitCode.ShouldBe(0);
            File.Exists(path).ShouldBeTrue();

            var script = await File.ReadAllTextAsync(path, ct);

            // The header sqlcmd needs and does not set for itself. Without it SQL Server refuses to
            // create a filtered index, the batch aborts, and sqlcmd still exits 0 — so its absence is
            // invisible right up until the schema is quietly wrong.
            script.Trim().ShouldStartWith("SET QUOTED_IDENTIFIER ON;");

            // The procedures, in the only form that can be run twice, in a batch of their own
            script.ShouldContain("uspDeleteIncomingEnvelopes");
            script.ShouldContain("CREATE OR ALTER PROCEDURE");
            BatchSeparator.IsMatch(script).ShouldBeTrue();

            // Empty the schema AFTER the dump, whatever the dump's own host did, so the first
            // execution below is genuinely creating everything.
            await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
            {
                await conn.OpenAsync(ct);
                await conn.ResetSchemaAsync(Schema, ct: ct);
            }

            await executeAsSqlcmdWouldAsync(script, ct);
            await assertNothingLeftToMigrateAsync(ct);

            // The half that used to fail, on the first unguarded object — and one failure aborts every
            // statement after it in the same batch.
            await executeAsSqlcmdWouldAsync(script, ct);
            await assertNothingLeftToMigrateAsync(ct);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     Run the script the way a file is run, one <c>GO</c> separated batch at a time. Handing the
    ///     whole text to one command is what answers "Incorrect syntax near 'GO'".
    /// </summary>
    private static async Task executeAsSqlcmdWouldAsync(string script, CancellationToken ct)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);

        foreach (var batch in BatchSeparator.Split(script))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    ///     The script built the configured schema, rather than merely running without error.
    /// </summary>
    private static async Task assertNothingLeftToMigrateAsync(CancellationToken ct)
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, Schema);

                // Nothing may provision anything here: this host is the assertion, not a second
                // chance to create what the script missed.
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            })
            .StartAsync(ct);

        var database = (IDatabase)host.Services.GetRequiredService<IMessageStore>();
        await database.AssertDatabaseMatchesConfigurationAsync();

        await host.StopAsync(ct);
    }
}
