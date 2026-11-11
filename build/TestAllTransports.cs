using System;
using System.Linq;
using Nuke.Common;
using Serilog;

partial class Build
{
    Target TestAllTransports => _ => _
        .DependsOn(Compile, DockerUp)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            // DockerUp has the compose Pulsar running, so point the Pulsar fixture at it exactly as
            // CIPulsar and LocalGate do. Unset, every worker process starts its own Testcontainers
            // broker (GH-3799), and on a loaded machine the supervisor killed the first as a worker
            // that never became usable before Pulsar's standalone image finished booting.
            Environment.SetEnvironmentVariable("WOLVERINE_PULSAR", "pulsar://localhost:6650");
            Environment.SetEnvironmentVariable("WOLVERINE_PULSAR_HTTP", "http://localhost:8080");

            isolateTransportDatabases();

            var transportsDir = RootDirectory / "src" / "Transports";
            RunAllTestsProjects(transportsDir);
        });

    /// <summary>
    /// Gives this run a Postgres and a SQL Server database of its own, recreated now, named after the
    /// checkout's directory, and hands them to every worker through the ConnectionStrings__postgres /
    /// ConnectionStrings__sqlserver variables Servers.cs reads first.
    ///
    /// <para>Every checkout on a machine shares the compose servers, and the transport suites' durable
    /// tests migrate the default database's wolverine / receiver / sender schemas. A worktree on a
    /// branch with a different envelope-table shape (a partitioned inbox with group_id, say) left
    /// them in a shape this branch's migration could not walk back ("unique constraint on partitioned
    /// table must include all partitioning columns"), failing durable SQS tests that had nothing to
    /// do with the change under test. On a CI runner the database is fresh either way.</para>
    /// </summary>
    void isolateTransportDatabases()
    {
        var checkout = new string(RootDirectory.Name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        var database = "wolverine_transports_" + checkout;
        if (database.Length > 63) database = database[..63];

        recreatePostgresDatabase(database);
        Environment.SetEnvironmentVariable("ConnectionStrings__postgres",
            new Npgsql.NpgsqlConnectionStringBuilder(PostgresConnectionString) { Database = database }.ConnectionString);

        recreateSqlServerDatabase(database);
        Environment.SetEnvironmentVariable("ConnectionStrings__sqlserver",
            new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerConnectionString)
            {
                InitialCatalog = database,
                MultipleActiveResultSets = true
            }.ConnectionString);

        Log.Information("Transport suites use their own database {Database} on Postgres and SQL Server", database);
    }
}
