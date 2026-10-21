using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

/// <summary>
/// GH-5108, the EF Core half of GH-4505. A logical deduplication claim on an EF Core transactional chain
/// (<see cref="TransactionMiddlewareMode.Eager" />, one DbContext, one message database) is written INSIDE
/// the DbContext's transaction rather than on a connection of its own.
/// </summary>
/// <remarks>
/// <para>
/// Before this, the claim was committed on its own connection before <c>BeginTransactionAsync</c> and only
/// given back when the chain threw. A process killed between the claim and the EF Core commit left the
/// claim committed and the work undone, so inbox recovery replayed the envelope into a "duplicate" and the
/// message was silently lost. A crash cannot be staged deterministically in a test, but what makes it
/// survivable can: <see cref="no_observer_outside_the_transaction_can_see_the_claim_until_the_handler_commits" />
/// reads the claim table from another connection while the handler is still running. Anything committed
/// at that point survives a kill.
/// </para>
/// </remarks>
public abstract class deduplication_rides_the_ef_core_transaction_5108 : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    protected const string SchemaName = "gh5108";
    private IHost _host = null!;

    protected deduplication_rides_the_ef_core_transaction_5108(ITestOutputHelper output)
    {
        _output = output;
    }

    protected abstract void configurePersistence(WolverineOptions opts);

    /// <summary>
    /// Counts the claims for <paramref name="id" /> from a connection of its own, skipping any row another
    /// transaction still holds rather than waiting on it.
    /// </summary>
    protected abstract Task<int> committedClaimCountAsync(string id);

    public async ValueTask InitializeAsync()
    {
        Handler5108.Reset();
        Handler5108.CountClaims = committedClaimCountAsync;

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();

                configurePersistence(opts);

                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                // The durable inbox, as in the issue
                opts.Policies.UseDurableLocalQueues();

                // Discard rather than retry, so each send is exactly one handler attempt
                opts.OnException<DivideByZeroException>().Discard();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Handler5108));

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        await _host.ResetResourceState();

        // ResetResourceState clears Wolverine's tables, not the application's
        using var scope = _host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Payment5108DbContext>().Payments
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var chain = _host.GetRuntime().Handlers.ChainFor<T>();
        chain.ShouldNotBeNull();
        chain.SourceCode.ShouldNotBeNull();
        _output.WriteLine(chain.SourceCode);
        return chain.SourceCode;
    }

    private async Task<int> paymentCountAsync(string name)
    {
        using var scope = _host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Payment5108DbContext>();
        return await dbContext.Payments.CountAsync(x => x.Name == name, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void the_claim_is_written_on_the_dbcontext_transaction_and_nothing_is_released()
    {
        var code = sourceFor<RecordPayment5108>();

        code.ShouldContain(".TryClaimDeduplicationIdAsync(");
        code.ShouldNotContain("_messageDeduplicator");
        code.ShouldNotContain("ReleaseAsync");
        code.ShouldNotContain("deduplicatedExecutionThrew");
    }

    [Fact]
    public async Task no_observer_outside_the_transaction_can_see_the_claim_until_the_handler_commits()
    {
        await _host.SendMessageAndWaitAsync(new ObservedPayment5108(),
            new DeliveryOptions { DeduplicationId = "observed" });

        Handler5108.ClaimsVisibleDuringHandler.ShouldBe(0,
            "the claim was committed before the EF Core transaction, so a process killed before the commit leaves the claim behind and the work undone");

        // ...and it is there once the transaction commits, so the check above is not passing because the
        // claim was never written at all
        (await committedClaimCountAsync("observed")).ShouldBe(1);
    }

    [Fact]
    public async Task the_first_message_runs_and_a_replay_of_the_same_id_is_discarded()
    {
        await _host.SendMessageAndWaitAsync(new RecordPayment5108("first"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        await _host.SendMessageAndWaitAsync(new RecordPayment5108("second"),
            new DeliveryOptions { DeduplicationId = "invoice-17" });

        Handler5108.Received.ShouldHaveSingleItem().ShouldBe("first");
        (await paymentCountAsync("first")).ShouldBe(1);
        (await paymentCountAsync("second")).ShouldBe(0);
        (await committedClaimCountAsync("invoice-17")).ShouldBe(1);
    }

    [Fact]
    public async Task different_logical_ids_both_run()
    {
        await _host.SendMessageAndWaitAsync(new RecordPayment5108("a"),
            new DeliveryOptions { DeduplicationId = "invoice-a" });
        await _host.SendMessageAndWaitAsync(new RecordPayment5108("b"),
            new DeliveryOptions { DeduplicationId = "invoice-b" });

        Handler5108.Received.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_handler_that_throws_never_claimed_the_id_at_all()
    {
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingPayment5108(),
                new DeliveryOptions { DeduplicationId = "poison" });

        Handler5108.FailedAttempts.ShouldBe(1);
        (await committedClaimCountAsync("poison")).ShouldBe(0);
        (await paymentCountAsync("doomed")).ShouldBe(0);

        await _host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingPayment5108(),
                new DeliveryOptions { DeduplicationId = "poison" });

        Handler5108.FailedAttempts.ShouldBe(2);
    }

    [Fact]
    public async Task an_unkeyed_message_on_an_optional_chain_still_runs()
    {
        var code = sourceFor<OptionallyKeyedPayment5108>();
        code.ShouldContain("if (!string.IsNullOrWhiteSpace(");

        await _host.SendMessageAndWaitAsync(new OptionallyKeyedPayment5108("x"));
        await _host.SendMessageAndWaitAsync(new OptionallyKeyedPayment5108("y"));

        Handler5108.Received.ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task concurrent_copies_of_one_id_run_the_handler_once()
    {
        // The second claim blocks on the first one's uncommitted row, then finds it committed and refuses.
        // Neither copy may surface as an exception: InvokeAsync rethrows.
        var bus = _host.MessageBus();
        var command = new RacingPayment5108();

        await Task.WhenAll(
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()),
            bus.InvokeAsync(command, TestContext.Current.CancellationToken, timeout: 30.Seconds()));

        Handler5108.RacingRuns.ShouldBe(1);
        (await paymentCountAsync("racing")).ShouldBe(1);
        (await committedClaimCountAsync(RacingPayment5108.Key)).ShouldBe(1);
    }
}

public class deduplication_rides_the_ef_core_transaction_on_postgresql_5108
    : deduplication_rides_the_ef_core_transaction_5108
{
    public deduplication_rides_the_ef_core_transaction_on_postgresql_5108(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<Payment5108DbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
    }

    protected override async Task<int> committedClaimCountAsync(string id)
    {
        // MVCC: an uncommitted row is simply invisible here, no waiting involved
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {SchemaName}.wolverine_deduplication_hashed where deduplication_id = @id";
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}

public class deduplication_rides_the_ef_core_transaction_on_sql_server_5108
    : deduplication_rides_the_ef_core_transaction_5108
{
    public deduplication_rides_the_ef_core_transaction_on_sql_server_5108(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<Payment5108DbContext>(x =>
            x.UseSqlServer(Servers.SqlServerConnectionString));
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SchemaName);
    }

    protected override async Task<int> committedClaimCountAsync(string id)
    {
        // READPAST rather than waiting: a row another transaction still holds is skipped, which is
        // exactly "not committed" -- and the handler doing the asking is the one holding it
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"select count(*) from {SchemaName}.wolverine_deduplication_hashed with (readpast) where deduplication_id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}

public class Payment5108
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// One DbContext type for both providers: EF Core caches a model per provider's internal service provider,
/// so the two hosts do not share one.
/// </summary>
public class Payment5108DbContext : DbContext
{
    public Payment5108DbContext(DbContextOptions<Payment5108DbContext> options) : base(options)
    {
    }

    public DbSet<Payment5108> Payments => Set<Payment5108>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment5108>(map =>
        {
            map.ToTable("payments", "gh5108_app");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

public record RecordPayment5108(string Name);

public record FailingPayment5108;

public record ObservedPayment5108;

public record OptionallyKeyedPayment5108(string Name);

[Deduplicated(Source = ValueSource.InputMember, Key = nameof(DeduplicationKey))]
public record RacingPayment5108
{
    public const string Key = "racing-5108";

    public string DeduplicationKey => Key;
}

public static class Handler5108
{
    public static readonly List<string> Received = [];
    public static int FailedAttempts;
    public static int RacingRuns;
    public static int ClaimsVisibleDuringHandler = -1;
    public static Func<string, Task<int>> CountClaims = _ => Task.FromResult(-1);

    public static void Reset()
    {
        Received.Clear();
        FailedAttempts = 0;
        RacingRuns = 0;
        ClaimsVisibleDuringHandler = -1;
    }

    // Each handler takes the DbContext, so AutoApplyTransactions puts it on the EF Core middleware

    [Deduplicated]
    public static void Handle(RecordPayment5108 message, Payment5108DbContext db)
    {
        Received.Add(message.Name);
        db.Payments.Add(new Payment5108 { Id = Guid.NewGuid(), Name = message.Name });
    }

    [Deduplicated]
    public static void Handle(FailingPayment5108 message, Payment5108DbContext db)
    {
        FailedAttempts++;

        // Written first, so the rollback is what takes the claim away
        db.Payments.Add(new Payment5108 { Id = Guid.NewGuid(), Name = "doomed" });

        throw new DivideByZeroException("nope");
    }

    [Deduplicated]
    public static async Task Handle(ObservedPayment5108 message, Payment5108DbContext db)
    {
        db.Payments.Add(new Payment5108 { Id = Guid.NewGuid(), Name = "observed" });
        ClaimsVisibleDuringHandler = await CountClaims("observed");
    }

    [Deduplicated(Required = false)]
    public static void Handle(OptionallyKeyedPayment5108 message, Payment5108DbContext db)
    {
        Received.Add(message.Name);
    }

    public static async Task Handle(RacingPayment5108 message, Payment5108DbContext db)
    {
        Interlocked.Increment(ref RacingRuns);
        db.Payments.Add(new Payment5108 { Id = Guid.NewGuid(), Name = "racing" });

        // Hold the transaction open so the second copy arrives while the first claim is uncommitted
        await Task.Delay(500);
    }
}

/// <summary>
/// GH-5108. Lightweight mode opens no explicit transaction, so there is nothing for the claim to ride and the
/// chain keeps claim-and-release.
/// </summary>
public class lightweight_ef_core_chains_keep_claim_and_release_5108
{
    private readonly ITestOutputHelper _output;

    public lightweight_ef_core_chains_keep_claim_and_release_5108(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task the_claim_is_written_up_front_and_released_on_failure()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;

                opts.Services.AddDbContextWithWolverineIntegration<Payment5108DbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gh5108");

                opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Lightweight);
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Handler5108));

                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        runtime.Handlers.HandlerFor<RecordPayment5108>();
        var code = runtime.Handlers.ChainFor<RecordPayment5108>()!.SourceCode!;
        _output.WriteLine(code);

        code.ShouldContain("_messageDeduplicator.TryClaimAsync(");
        code.ShouldContain("ReleaseAsync");
        code.ShouldNotContain("TryClaimDeduplicationIdAsync");
    }
}
