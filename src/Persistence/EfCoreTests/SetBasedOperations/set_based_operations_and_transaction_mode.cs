using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.SqlServer;

namespace EfCoreTests.SetBasedOperations;

/// <summary>
///     GH-4629. The one rule the EF Core documentation asks you to carry by hand: a set-based
///     operation -- <c>ExecuteUpdateAsync</c>, <c>ExecuteDeleteAsync</c>, <c>Database.ExecuteSqlAsync</c>
///     -- is its own statement, not a change the <c>DbContext</c> is tracking. In
///     <see cref="TransactionMiddlewareMode.Eager" /> it runs inside the transaction Wolverine opened
///     for the handler, so a later failure takes it back out again. In
///     <see cref="TransactionMiddlewareMode.Lightweight" /> there is no such transaction and the
///     statement commits on its own, leaving the write behind after the handler blows up and the
///     outbox rolls back.
///     <para>
///         Neither half of that rule had a test in either mode. These pin it as it stands, and they
///         are the acceptance test for the <c>EfCoreOps</c> family that makes the rule automatic.
///     </para>
/// </summary>
public abstract class set_based_operations_and_transaction_mode
{
    protected abstract void configurePersistence(WolverineOptions opts);

    private async Task<IHost> startHostAsync(TransactionMiddlewareMode mode)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                configurePersistence(opts);

                opts.UseEntityFrameworkCoreTransactions(mode);
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<RawSetBasedHandlers>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> seedAsync(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();

        var record = new OpsRecord { Id = Guid.NewGuid(), Name = "seeded", Tally = 1 };
        context.Records.Add(record);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return record.Id;
    }

    private static async Task<OpsRecord?> loadAsync(IHost host, Guid id)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();
        return await context.Records.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    private static async Task invokeExpectingFailureAsync(IHost host, object message)
    {
        try
        {
            await host.MessageBus().InvokeAsync(message, TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            return;
        }

        throw new Exception($"Expected {message} to fail in its handler, but it succeeded");
    }

    [Fact]
    public async Task execute_update_inside_a_handler_rolls_back_with_the_handler_in_eager_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Eager);
        var id = await seedAsync(host);

        await invokeExpectingFailureAsync(host, new ArchiveRecord(id));

        var record = await loadAsync(host, id);
        record.ShouldNotBeNull();
        record.Archived.ShouldBeFalse();
    }

    [Fact]
    public async Task execute_update_inside_a_handler_auto_commits_independently_in_lightweight_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight);
        var id = await seedAsync(host);

        await invokeExpectingFailureAsync(host, new ArchiveRecord(id));

        // Nothing rolled the statement back, because nothing ever began a transaction around it
        var record = await loadAsync(host, id);
        record.ShouldNotBeNull();
        record.Archived.ShouldBeTrue();
    }

    [Fact]
    public async Task execute_delete_inside_a_handler_rolls_back_with_the_handler_in_eager_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Eager);
        var id = await seedAsync(host);

        await invokeExpectingFailureAsync(host, new PurgeRecord(id));

        (await loadAsync(host, id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task execute_delete_inside_a_handler_auto_commits_independently_in_lightweight_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight);
        var id = await seedAsync(host);

        await invokeExpectingFailureAsync(host, new PurgeRecord(id));

        (await loadAsync(host, id)).ShouldBeNull();
    }

    [Fact]
    public async Task execute_sql_inside_a_handler_rolls_back_with_the_handler_in_eager_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Eager);
        var id = await seedAsync(host);

        await invokeExpectingFailureAsync(host, new BumpTally(id));

        var record = await loadAsync(host, id);
        record.ShouldNotBeNull();
        record.Tally.ShouldBe(1);
    }

    [Fact]
    public async Task execute_sql_inside_a_handler_auto_commits_independently_in_lightweight_mode()
    {
        using var host = await startHostAsync(TransactionMiddlewareMode.Lightweight);
        var id = await seedAsync(host);

        await invokeExpectingFailureAsync(host, new BumpTally(id));

        var record = await loadAsync(host, id);
        record.ShouldNotBeNull();
        record.Tally.ShouldBe(2);
    }
}

[Collection("postgresql")]
public class set_based_operations_on_postgresql : set_based_operations_and_transaction_mode
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<OpsDbContext>(
            x => x.UseNpgsql(Servers.PostgresConnectionString), "efcore_ops");

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "efcore_ops");
    }
}

[Collection("sqlserver")]
public class set_based_operations_on_sqlserver : set_based_operations_and_transaction_mode
{
    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<OpsDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString), "efcore_ops");

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "efcore_ops");
    }
}

public record ArchiveRecord(Guid Id);

public record PurgeRecord(Guid Id);

public record BumpTally(Guid Id);

public class RawSetBasedHandlers
{
    public static async Task Handle(ArchiveRecord command, OpsDbContext db, CancellationToken token)
    {
        await db.Records.Where(x => x.Id == command.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Archived, true), token);

        throw new DeliberateSetBasedFailure();
    }

    public static async Task Handle(PurgeRecord command, OpsDbContext db, CancellationToken token)
    {
        await db.Records.Where(x => x.Id == command.Id).ExecuteDeleteAsync(token);

        throw new DeliberateSetBasedFailure();
    }

    public static async Task Handle(BumpTally command, OpsDbContext db, CancellationToken token)
    {
        await db.Database.ExecuteSqlAsync(
            $"update ops_records set tally = tally + 1 where id = {command.Id}", token);

        throw new DeliberateSetBasedFailure();
    }
}
