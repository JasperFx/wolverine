using IntegrationTests;
using JasperFx;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql.Transport;
using Wolverine.Runtime;
using Xunit;

namespace PostgresqlTests.Transport;

// Coverage for the StickyPostgresqlQueueListenerAgent health-check enrichment
// added in #2647. The agent layers per-tenant DB reachability, listener-latch
// state, and per-tenant queue depth on top of the default Status-based check.
//
// Pure logic (status precedence, no-endpoint case) is covered by the
// no-runtime test class. The reachability + queue-depth signals genuinely need
// a Postgres connection, so those live in the [Collection("postgresql")] class
// using PostgresqlContext.
public class sticky_listener_health_tests_no_runtime
{
    private static StickyPostgresqlQueueListenerAgent agent_with_status(AgentStatus status)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());

        var agent = new StickyPostgresqlQueueListenerAgent(runtime, "messages", "tenant1");
        agent.Status = status;
        return agent;
    }

    [Fact]
    public async Task unhealthy_when_status_is_not_running()
    {
        var agent = agent_with_status(AgentStatus.Stopped);

        var result = await agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("Stopped");
    }

    [Fact]
    public async Task healthy_when_running_and_endpoint_not_yet_built()
    {
        // Before StartAsync wires up _tenantEndpoint there's nothing to ping; the
        // agent should report Healthy rather than crash on a null deref.
        var agent = agent_with_status(AgentStatus.Running);

        var result = await agent.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.ShouldBe(HealthStatus.Healthy);
        agent.ConsecutiveDbFailureCount.ShouldBe(0);
    }

    [Fact]
    public void agent_description_mentions_tenant_database_and_queue_name()
    {
        var agent = agent_with_status(AgentStatus.Running);

        agent.Description.ShouldContain("tenant1");
        agent.Description.ShouldContain("messages");
    }
}

[Collection("postgresql")]
public class sticky_listener_health_db_tests : PostgresqlContext, IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private TenantedPostgresqlQueue _endpoint = null!;
    private PostgresqlQueue _parentQueue = null!;
    private PostgresqlTransport _transport = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _transport = new PostgresqlTransport();
        _parentQueue = new PostgresqlQueue("stickyhealth", _transport);

        _dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        _endpoint = new TenantedPostgresqlQueue(_parentQueue, _dataSource, "tenantA");

        _tableName = _parentQueue.QueueTable.Identifier.QualifiedName;

        // Drop+recreate so each test starts from a known empty state.
        await using var conn = await _dataSource.OpenConnectionAsync();
        try
        {
            var drop = conn.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS {_tableName} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }

        await _parentQueue.EnsureSchemaExists("tenantA", _dataSource);
    }

    public async Task DisposeAsync()
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        try
        {
            var drop = conn.CreateCommand();
            drop.CommandText = $"DROP TABLE IF EXISTS {_tableName} CASCADE";
            await drop.ExecuteNonQueryAsync();
        }
        finally
        {
            await conn.CloseAsync();
        }

        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task ping_database_succeeds_against_real_postgres()
    {
        // Smoke - the SELECT 1 ping that the sticky-listener agent uses for the
        // per-tenant DB reachability signal must not throw against a healthy DB.
        await Should.NotThrowAsync(() => _endpoint.PingDatabaseAsync(CancellationToken.None));
    }

    [Fact]
    public async Task get_queue_depth_returns_zero_for_empty_table()
    {
        (await _endpoint.GetQueueDepthAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task get_queue_depth_reflects_inserted_rows()
    {
        await using (var conn = await _dataSource.OpenConnectionAsync())
        {
            try
            {
                for (var i = 0; i < 7; i++)
                {
                    var insert = conn.CreateCommand();
                    insert.CommandText =
                        $"INSERT INTO {_tableName} (id, body, message_type, keep_until) " +
                        "VALUES (gen_random_uuid(), '\\x00'::bytea, 'TestMessage', null)";
                    await insert.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await conn.CloseAsync();
            }
        }

        (await _endpoint.GetQueueDepthAsync(CancellationToken.None)).ShouldBe(7);
    }

    [Fact]
    public async Task ping_database_throws_against_unreachable_database()
    {
        // Point at a deliberately broken connection string. A failure here is the
        // signal source the sticky-listener health check turns into Degraded /
        // Unhealthy via its consecutive-failure counter.
        var unreachable = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;password=nope;Timeout=2;Command Timeout=2;Pooling=false");

        try
        {
            var endpoint = new TenantedPostgresqlQueue(_parentQueue, unreachable, "missing");
            await Should.ThrowAsync<Exception>(
                () => endpoint.PingDatabaseAsync(CancellationToken.None));
        }
        finally
        {
            await unreachable.DisposeAsync();
        }
    }
}
