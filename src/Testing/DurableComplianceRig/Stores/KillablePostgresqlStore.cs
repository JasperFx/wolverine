using DurableComplianceRig.Infrastructure;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Postgresql;

namespace DurableComplianceRig.Stores;

/// <summary>
/// Wolverine's PostgreSQL message store, with every node logging in as a role of its own so that one node can be
/// killed without touching the others: the role loses its login and every one of its sessions is terminated. That is
/// as close to a dead process as a test process gets -- nothing the node does afterwards reaches the database, so its
/// rows stay owned, its node record goes stale, its advisory locks are gone, and its handlers cannot commit.
/// </summary>
/// <remarks>
/// <c>IHost.Dispose()</c> is not a crash: it runs <c>WolverineRuntime.DisposeAsync</c>, which runs the whole graceful
/// <c>StopAsync</c> -- drain, release every owned row, delete the node record.
/// </remarks>
public class KillablePostgresqlStore : PostgresqlStore, IKillableStore, IInboxRowBlocker
{
    private const int MostNodes = 4;
    private const string Password = "rig";
    private int _configured;

    private static string roleFor(int node) =>
        RigEnvironment.Suffix.Length == 0 ? $"rig_node{node}" : $"rig_{RigEnvironment.Suffix}_node{node}";

    private static string connectionStringFor(int node) =>
        new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Username = roleFor(node),
            Password = Password,
            ApplicationName = $"durable-rig node {node}",

            // Act as the default user once logged in, so every node owns what any node creates. The login (the
            // session user, which is what a kill matches on) stays the node's own
            Options = $"-c role={adminUser}"
        }.ConnectionString;

    private static string adminUser => new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString).Username!;

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        // Called once per node, in the order the nodes start
        var node = Interlocked.Increment(ref _configured);
        if (node > MostNodes) throw new InvalidOperationException($"Only {MostNodes} killable nodes are prepared");

        var connectionString = connectionStringFor(node);
        var persistence = opts.PersistMessagesWithPostgresql(connectionString, Schema);
        if (withDatabaseQueues) persistence.EnableMessageTransport(x => x.AutoProvision());

        // The handlers write their effect as the node too, so a killed node's handler cannot commit one
        var nodeDatabase = new RigDatabase("postgresql", Dialect.Postgresql, () => new NpgsqlConnection(connectionString),
            Schema);
        opts.Services.AddSingleton(new RdbmsLedger(nodeDatabase));
        opts.Discovery.IncludeType(typeof(RdbmsRigHandler));
    }

    public async Task PrepareNodesAsync()
    {
        _configured = 0;
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        for (var node = 1; node <= MostNodes; node++)
        {
            var role = roleFor(node);

            // Membership lets the node act as the default user (see connectionStringFor); objects an earlier run
            // created as a node go back to the default user
            await executeAsync(conn, $"""
                                      do $$ begin
                                          if not exists (select 1 from pg_roles where rolname = '{role}') then
                                              create role {role} login password '{Password}';
                                          end if;
                                      end $$;
                                      alter role {role} login;
                                      grant {adminUser} to {role};
                                      reassign owned by {role} to {adminUser};
                                      """);
        }
    }

    public async Task KillNodeAsync(int node)
    {
        var role = roleFor(node);
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        await executeAsync(conn, $"""
                                  alter role {role} nologin;
                                  select pg_terminate_backend(pid) from pg_stat_activity where usename = '{role}';
                                  """);
    }

    public async Task ReviveAllAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        for (var node = 1; node <= MostNodes; node++)
        {
            await executeAsync(conn, $"alter role {roleFor(node)} login");
        }
    }

    /// <summary>
    /// A blip on one row, rather than the whole database: the row is locked from a session of the rig's own, and every
    /// other session that waits on it is terminated, so each write Wolverine attempts to it fails the way a dropped
    /// connection does. Everything else keeps working. Only node sessions are cut (by application name), and the only
    /// lock the rig holds is this one, so a node session waiting on a lock is waiting on this row.
    /// </summary>
    public async Task<IAsyncDisposable> BlockInboxRowAsync(Guid id, Action<string> cut)
    {
        var holder = new NpgsqlConnection(Servers.PostgresConnectionString);
        await holder.OpenAsync();
        var tx = await holder.BeginTransactionAsync();

        await using (var cmd = holder.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"select count(*) from (select id from {Schema}.wolverine_incoming_envelopes where id = @id for update) locked";
            cmd.Parameters.AddWithValue("id", id);
            var locked = (long)(await cmd.ExecuteScalarAsync())!;
            if (locked == 0) throw new InvalidOperationException($"There is no inbox row {id} to block");
        }

        var stop = new CancellationTokenSource();
        var cutter = Task.Run(async () =>
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            while (!stop.IsCancellationRequested)
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                                       select pg_terminate_backend(pid), left(query, 160) from pg_stat_activity
                                       where pid <> pg_backend_pid() and pid <> @holder and wait_event_type = 'Lock'
                                         and application_name like 'durable-rig node%'
                                       """;
                    cmd.Parameters.AddWithValue("holder", holder.ProcessID);
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) cut(reader.GetString(1).ReplaceLineEndings(" "));
                }

                try
                {
                    await Task.Delay(25, stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        return new RowBlock(holder, tx, stop, cutter);
    }

    private sealed class RowBlock(NpgsqlConnection holder, NpgsqlTransaction tx, CancellationTokenSource stop, Task cutter)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
#pragma warning disable VSTHRD003 // started by this object, stopped here
            await cutter;
#pragma warning restore VSTHRD003
            await tx.RollbackAsync();
            await holder.DisposeAsync();
            stop.Dispose();
        }
    }

    private static async Task executeAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
