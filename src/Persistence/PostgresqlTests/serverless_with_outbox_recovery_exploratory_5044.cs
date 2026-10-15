using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Wolverine.Runtime;
using Wolverine.Persistence.Durability;
using Npgsql;
using Wolverine.ComplianceTests.Serverless;

namespace PostgresqlTests;

/// <summary>
/// PR #5044 exploratory testing: ServerlessWithOutbox recovery with the postgresql store, against real function instances
/// killed with SIGKILL. See <see cref="ServerlessOutboxRecoveryExploratory" />. Build the crash host first:
/// dotnet build src/Testing/ServerlessOutboxCrashHost -c Release -f net9.0
/// </summary>
public class serverless_with_outbox_recovery_exploratory_5044 : ServerlessOutboxRecoveryExploratory
{
    protected override string StoreName => "postgresql";
    protected override string SchemaName => "explore5044_rec_pg";
    protected override string BusinessTable => "exploresaga_saga";

    protected override async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}

/// <summary>
/// PR #5044 exploratory testing: the outbox stale clock is supposed to keep a durability pass away from a row
/// whose request may still be about to send it inline. A transaction that ran for longer than OutboxStaleTime
/// before it wrote and committed its outbox row must not hand that row to a pass the instant it commits.
/// </summary>
public class serverless_with_outbox_stale_clock_exploratory_5044 : IAsyncLifetime
{
    private const string SchemaName = "explore5044_rec_clock";
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.OutboxStaleTime = 2.Seconds();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            }).StartAsync(TestContext.Current.CancellationToken);

        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_row_committed_by_a_long_transaction_is_not_stale_the_moment_it_commits()
    {
        var store = (IMessageDatabase)theHost.GetRuntime().Storage;
        var envelope = ObjectMother.Envelope();
        envelope.Destination = "stub://clock".ToUri();
        envelope.DeliverBy = null;

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

            // The handler's transaction is open for longer than OutboxStaleTime before the cascade is persisted,
            // like a slow handler inside the transaction a lightweight saga or an EF Core Eager transaction opens
            await using (System.Data.Common.DbCommand sleep = conn.CreateCommand())
            {
                sleep.Transaction = tx;
                sleep.CommandText = "select pg_sleep(3)";
                await sleep.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await store.StoreOutgoingAsync(tx, [envelope]);
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }

        // The request that committed is about to send the row inline right now. A pass started at the same moment
        // must leave it alone, or the message goes out twice
        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), TestContext.Current.CancellationToken);

        report.OutgoingSent.ShouldBe(0,
            "the stale clock started when the transaction began, not when the row was written");
    }
}

/// <summary>
/// PR #5044 exploratory testing: a durability pass against a broker that accepts the connection and never answers.
/// MaxDuration only stops new work, so the question is whether the caller's CancellationToken bounds the pass
/// </summary>
public class serverless_with_outbox_hung_broker_exploratory_5044 : IAsyncLifetime
{
    private const string SchemaName = "explore5044_rec_hung";
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Durability.OutboxStaleTime = 1.Seconds();
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
            }).StartAsync(TestContext.Current.CancellationToken);

        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task a_pass_against_a_hung_broker_honors_the_callers_cancellation()
    {
        using var blackhole = new Wolverine.ComplianceTests.Serverless.Blackhole(
            Wolverine.ComplianceTests.Serverless.CrashHost.FreePort());

        var envelope = ObjectMother.Envelope();
        envelope.Destination = $"tcp://localhost:{blackhole.Port}".ToUri();
        envelope.DeliverBy = null;
        await theHost.GetRuntime().Storage.Outbox.StoreOutgoingAsync(envelope, 0);
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource(3.Seconds());
        var pass = theHost.RunDurabilityPassAsync(new DurabilityPassOptions { MaxDuration = 2.Seconds() },
            cancellation.Token);

        await blackhole.FirstData.Task.WaitAsync(30.Seconds(), TestContext.Current.CancellationToken);

        // Cancelled after 3 seconds, so a pass that honors the token is over well within 15
        var finished = await Task.WhenAny(pass, Task.Delay(15.Seconds(), TestContext.Current.CancellationToken));
        finished.ShouldBe(pass, "the pass is still blocked in the broker send 15 seconds after its token was cancelled");
    }
}
