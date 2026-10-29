using System.Transactions;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace PostgresqlTests;

/// <summary>
/// GH-5279. DurabilitySettings.UseTransactionScopedDurabilityPassLock. Behind a transaction-mode connection pooler a
/// session-level advisory lock can be taken on one server connection and released on another, so PostgreSQL can
/// take the durability pass lock with pg_try_advisory_xact_lock inside a transaction that is open for the pass
/// instead. The default stays session scoped, see GH-3664.
/// </summary>
public class transaction_scoped_durability_pass_lock : IAsyncLifetime
{
    private const string SchemaName = "serverless_xact_lock";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;
    private PostgresqlMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await buildHostAsync(SchemaName, opts => opts.Durability.UseTransactionScopedDurabilityPassLock = true);
        theStore = theHost.GetRuntime().Storage.As<PostgresqlMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static async Task<IHost> buildHostAsync(string schemaName, Action<WolverineOptions> configure,
        string? connectionString = null, bool rebuild = true)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(connectionString ?? Servers.PostgresConnectionString, schemaName);
                configure(opts);
            }).StartAsync();

        if (rebuild)
        {
            await host.RebuildAllEnvelopeStorageAsync();
        }

        return host;
    }

    // Stops the host even when an assertion fails, so its lock connection and data source never outlive the test.
    // IHost.Dispose() alone does not call StopAsync()
    private static async Task withHostAsync(IHost host, Func<Task> test)
    {
        try
        {
            await test();
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task<int> lockHolderPidAsync(int lockId)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var pid = await conn.CreateCommand(
                "select l.pid from pg_locks l where l.locktype = 'advisory' and l.granted and l.objsubid = 1 and ((l.classid::bigint << 32) | l.objid::bigint) = :id")
            .With("id", (long)lockId)
            .ExecuteScalarAsync();

        return Convert.ToInt32(pid);
    }

    // The pg_stat_activity state of every session holding the advisory lock. A bigint advisory key is split
    // into classid (high 32 bits) and objid (low 32 bits) in pg_locks
    private static async Task<string[]> lockHolderStatesAsync(int lockId)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var states = new List<string>();
        await using var reader = await conn.CreateCommand(
                "select a.state from pg_locks l join pg_stat_activity a on a.pid = l.pid where l.locktype = 'advisory' and l.granted and l.objsubid = 1 and ((l.classid::bigint << 32) | l.objid::bigint) = :id")
            .With("id", (long)lockId)
            .ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            states.Add(await reader.GetFieldValueAsync<string>(0));
        }

        return states.ToArray();
    }

    private async Task<Envelope> storeStaleOutgoingAsync(string destination)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = destination.ToUri();
        envelope.DeliverBy = null;

        await theStore.StoreOutgoingAsync(envelope, 0);

        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.CreateCommand(
                $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = :time where id = :id")
            .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
            .With("id", envelope.Id)
            .ExecuteNonQueryAsync();

        return envelope;
    }

    [Fact]
    public void the_default_is_a_session_scoped_lock()
    {
        new DurabilitySettings().UseTransactionScopedDurabilityPassLock.ShouldBeFalse();
    }

    [Fact]
    public async Task the_lock_is_held_in_an_open_transaction_and_is_exclusive()
    {
        var first = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        first.ShouldNotBeNull();

        try
        {
            (await lockHolderStatesAsync(theStore.DurabilityPassLockId)).ShouldBe(["idle in transaction"]);

            (await theStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
        }
        finally
        {
            await first.DisposeAsync();
        }

        (await lockHolderStatesAsync(theStore.DurabilityPassLockId)).ShouldBeEmpty();

        var again = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        again.ShouldNotBeNull();
        await again.DisposeAsync();
    }

    [Fact]
    public async Task a_pass_skips_the_store_while_another_pass_holds_its_lock()
    {
        var envelope = await storeStaleOutgoingAsync("stub://xact-locked");

        await using (await theStore.TryAcquireDurabilityPassLockAsync(Token))
        {
            var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

            report.SkippedStores.ShouldContain(theStore.Uri);
            report.Stores.Single(x => x.StoreUri == theStore.Uri).Lock.ShouldBe(DurabilityPassLockStatus.HeldElsewhere);
            report.OutgoingSent.ShouldBe(0);
        }

        theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>().Endpoints["xact-locked"].Sent
            .ShouldBeEmpty();

        // The next pass gets the lock and sends it
        var next = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        next.OutgoingSent.ShouldBe(1);
        theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>().Endpoints["xact-locked"].Sent
            .Single().Id.ShouldBe(envelope.Id);
    }

    [Fact]
    public async Task a_pass_runs_under_the_lock_and_releases_it()
    {
        await storeStaleOutgoingAsync("stub://xact-pass");

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.Stores.Single(x => x.StoreUri == theStore.Uri).Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        (await lockHolderStatesAsync(theStore.DurabilityPassLockId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task the_lock_is_released_when_a_step_throws(bool transactionScoped)
    {
        var schemaName = transactionScoped ? "serverless_xact_lock_throws" : "serverless_session_lock_throws";
        var host = await buildHostAsync(schemaName, opts =>
        {
            opts.Durability.UseTransactionScopedDurabilityPassLock = transactionScoped;
            opts.Services.AddSingleton<IDurabilityPassStep>(new ThrowingStep());
        });

        await withHostAsync(host, async () =>
        {
            var store = host.GetRuntime().Storage.As<PostgresqlMessageStore>();

            var report = await host.RunDurabilityPassAsync(30.Seconds(), Token);

            report.FailedSteps.ShouldBe(1);
            report.Stores.Single(x => x.StoreUri == store.Uri).Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

            (await lockHolderStatesAsync(store.DurabilityPassLockId)).ShouldBeEmpty();

            var again = await store.TryAcquireDurabilityPassLockAsync(Token);
            again.ShouldNotBeNull();
            await again.DisposeAsync();
        });
    }

    [Fact]
    public async Task by_default_the_lock_is_session_scoped_with_no_transaction_open()
    {
        var host = await buildHostAsync("serverless_session_lock", _ => { });

        await withHostAsync(host, async () =>
        {
            var store = host.GetRuntime().Storage.As<PostgresqlMessageStore>();

            var first = await store.TryAcquireDurabilityPassLockAsync(Token);
            first.ShouldNotBeNull();

            try
            {
                (await lockHolderStatesAsync(store.DurabilityPassLockId)).ShouldBe(["idle"]);

                (await store.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
            }
            finally
            {
                await first.DisposeAsync();
            }

            (await lockHolderStatesAsync(store.DurabilityPassLockId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task the_session_lock_and_the_transaction_scoped_lock_exclude_each_other()
    {
        // During a rolling change one function instance can still take the session lock while another already
        // takes the transaction-scoped one. Both are the same bigint advisory key, so they must exclude each other
        var session = await buildHostAsync(SchemaName, _ => { }, rebuild: false);

        await withHostAsync(session, async () =>
        {
            var sessionStore = session.GetRuntime().Storage.As<PostgresqlMessageStore>();
            sessionStore.DurabilityPassLockId.ShouldBe(theStore.DurabilityPassLockId);

            await using (var held = await sessionStore.TryAcquireDurabilityPassLockAsync(Token))
            {
                held.ShouldNotBeNull();
                (await theStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
            }

            await using (var held = await theStore.TryAcquireDurabilityPassLockAsync(Token))
            {
                held.ShouldNotBeNull();
                (await sessionStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
            }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task the_lock_connection_stays_out_of_an_ambient_transaction_scope(bool transactionScoped)
    {
        // With Enlist=true the lock connection used to join the caller's TransactionScope, and then
        // BeginTransactionAsync threw because a transaction was already in progress
        var schemaName = transactionScoped ? "serverless_xact_lock_ambient" : "serverless_session_lock_ambient";
        var connectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString) { Enlist = true }
            .ConnectionString;

        var host = await buildHostAsync(schemaName,
            opts => opts.Durability.UseTransactionScopedDurabilityPassLock = transactionScoped, connectionString);

        await withHostAsync(host, async () =>
        {
            var store = host.GetRuntime().Storage.As<PostgresqlMessageStore>();

            using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var held = await store.TryAcquireDurabilityPassLockAsync(Token);
                held.ShouldNotBeNull();
                await held.DisposeAsync();
            }

            (await lockHolderStatesAsync(store.DurabilityPassLockId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task an_idle_in_transaction_timeout_does_not_release_the_lock_during_the_pass()
    {
        // A server-side idle_in_transaction_session_timeout shorter than a pass used to end the lock transaction
        // while the pass was still running, and a second pass then got the lock too
        var connectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Options = "-c idle_in_transaction_session_timeout=1s"
        }.ConnectionString;

        var host = await buildHostAsync("serverless_xact_lock_idle_timeout",
            opts => opts.Durability.UseTransactionScopedDurabilityPassLock = true, connectionString);

        await withHostAsync(host, async () =>
        {
            var store = host.GetRuntime().Storage.As<PostgresqlMessageStore>();

            var held = await store.TryAcquireDurabilityPassLockAsync(Token);
            held.ShouldNotBeNull();

            try
            {
                await Task.Delay(2500.Milliseconds(), Token);

                (await lockHolderStatesAsync(store.DurabilityPassLockId)).ShouldBe(["idle in transaction"]);
                (await store.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
            }
            finally
            {
                await held.DisposeAsync();
            }

            (await lockHolderStatesAsync(store.DurabilityPassLockId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task losing_the_lock_transaction_during_the_pass_is_reported_when_the_lock_is_released()
    {
        // What PgBouncer's idle_transaction_timeout, a failover or a killed backend does to the lock session
        var held = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        var pid = await lockHolderPidAsync(theStore.DurabilityPassLockId);
        await using (var admin = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await admin.OpenAsync(Token);
            await admin.CreateCommand("select pg_terminate_backend(:pid)").With("pid", pid).ExecuteNonQueryAsync(Token);
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await held.DisposeAsync());
        ex.Message.ShouldContain("ended before the pass did");
        ex.InnerException.ShouldNotBeNull();

        // And the broken connection did not go back to the pool to poison the next pass
        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        report.Stores.Single(x => x.StoreUri == theStore.Uri).Lock.ShouldBe(DurabilityPassLockStatus.Acquired);
        report.FailedSteps.ShouldBe(0);
    }

    public class ThrowingStep : IDurabilityPassStep
    {
        public string Name => "Throws";

        public Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
        {
            throw new DivideByZeroException("Boom!");
        }
    }
}
