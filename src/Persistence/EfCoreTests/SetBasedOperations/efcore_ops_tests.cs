using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests.SetBasedOperations;

/// <summary>
///     GH-4629. The declarative <see cref="EfCoreOp" /> family, and the rule it exists to enforce:
///     a chain returning an operation that writes outside <c>SaveChangesAsync</c> runs in
///     <see cref="TransactionMiddlewareMode.Eager" /> whatever the application configured.
/// </summary>
[Collection("postgresql")]
public class efcore_ops_tests : IAsyncLifetime
{
    private IHost _lightweight = null!;

    public async ValueTask InitializeAsync()
    {
        _lightweight = await startHostAsync(TransactionMiddlewareMode.Lightweight);

        using var scope = _lightweight.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();
        await context.Records.ExecuteDeleteAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _lightweight.StopAsync(CancellationToken.None);
        _lightweight.Dispose();
    }

    private static async Task<IHost> startHostAsync(TransactionMiddlewareMode mode)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<OpsDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString)
                        .AddInterceptors(OpsCommandRecorder.Instance, OpsSaveChangesRecorder.Instance),
                    "efcore_ops");

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "efcore_ops");

                opts.UseEntityFrameworkCoreTransactions(mode);
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<EfCoreOpHandlers>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<OpsRecord?> loadAsync(Guid id)
    {
        using var scope = _lightweight.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();
        return await context.Records.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task seedAsync(params OpsRecord[] records)
    {
        using var scope = _lightweight.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();
        await context.Records.AddRangeAsync(records, TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task execute_update_op_applies_the_update()
    {
        var id = Guid.NewGuid();
        await seedAsync(new OpsRecord { Id = id, Name = "keep" });

        await _lightweight.MessageBus()
            .InvokeAsync(new ArchiveViaOp(id), TestContext.Current.CancellationToken);

        (await loadAsync(id))!.Archived.ShouldBeTrue();
    }

    [Fact]
    public async Task execute_delete_op_deletes_the_rows()
    {
        var id = Guid.NewGuid();
        await seedAsync(new OpsRecord { Id = id, Name = "doomed" });

        await _lightweight.MessageBus()
            .InvokeAsync(new PurgeViaOp(id), TestContext.Current.CancellationToken);

        (await loadAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task execute_sql_op_runs_the_statement()
    {
        var id = Guid.NewGuid();
        await seedAsync(new OpsRecord { Id = id, Name = "counted", Tally = 1 });

        await _lightweight.MessageBus()
            .InvokeAsync(new BumpTallyViaOp(id), TestContext.Current.CancellationToken);

        (await loadAsync(id))!.Tally.ShouldBe(2);
    }

    [Fact]
    public async Task returning_an_efcore_op_forces_eager_even_when_the_default_is_lightweight()
    {
        // Codegen half: the application asked for Lightweight, and the chain got the eager
        // transaction frames anyway.
        _lightweight.GetRuntime().Handlers.HandlerFor<ArchiveViaOp>();
        var chain = _lightweight.GetRuntime().Handlers.ChainFor<ArchiveViaOp>()!;

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task returning_an_efcore_op_rolls_the_statement_back_when_the_handler_fails()
    {
        // Behavioural half of the same rule. The application default is Lightweight, so without the
        // forced eager transaction the ExecuteUpdate would commit on its own and survive the
        // DbUpdateException that the colliding insert raises out of SaveChangesAsync.
        var id = Guid.NewGuid();
        await seedAsync(new OpsRecord { Id = id, Name = "survivor" });

        try
        {
            await _lightweight.MessageBus()
                .InvokeAsync(new ArchiveAndCollide(id), TestContext.Current.CancellationToken);
        }
        catch (Exception)
        {
            // The colliding insert is the point
        }

        (await loadAsync(id))!.Archived.ShouldBeFalse();
    }

    [Fact]
    public void an_op_that_writes_through_save_changes_does_not_force_eager()
    {
        // InsertMany is an AddRange -- it rides the same SaveChangesAsync as everything else, so it
        // has no reason to overrule the application's Lightweight default.
        _lightweight.GetRuntime().Handlers.HandlerFor<ImportBatch>();
        var chain = _lightweight.GetRuntime().Handlers.ChainFor<ImportBatch>()!;

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldBeEmpty();
    }

    [Fact]
    public void requires_eager_transaction_attribute_forces_eager()
    {
        _lightweight.GetRuntime().Handlers.HandlerFor<CallTheDatabaseDirectly>();
        var chain = _lightweight.GetRuntime().Handlers.ChainFor<CallTheDatabaseDirectly>()!;

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task insert_many_of_10k_rows_is_one_save_changes()
    {
        OpsSaveChangesRecorder.Start();
        try
        {
            await _lightweight.MessageBus()
                .InvokeAsync(new ImportBatch(10_000), TestContext.Current.CancellationToken);
        }
        finally
        {
            var saves = OpsSaveChangesRecorder.Stop();
            saves.ShouldBe(1);
        }

        using var scope = _lightweight.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();
        (await context.Records.CountAsync(x => x.Name == "imported", TestContext.Current.CancellationToken))
            .ShouldBe(10_000);
    }

    [Fact]
    public async Task unit_of_work_store_of_1k_entities_issues_one_existence_query()
    {
        // Half already in the database, half brand new, so the unit of work genuinely has to decide
        // insert-or-update for each one.
        var existing = Enumerable.Range(0, 500)
            .Select(i => new OpsRecord { Id = Guid.NewGuid(), Name = "uow-existing-" + i })
            .ToArray();

        await seedAsync(existing);

        var ids = existing.Select(x => x.Id)
            .Concat(Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()))
            .ToArray();

        OpsCommandRecorder.Start();
        string[] commands;
        try
        {
            await _lightweight.MessageBus()
                .InvokeAsync(new StoreManyRecords(ids), TestContext.Current.CancellationToken);
        }
        finally
        {
            commands = OpsCommandRecorder.Stop();
        }

        var existenceQueries = commands
            .Where(x => x.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Contains("ops_records", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        existenceQueries.Length.ShouldBe(1);

        using var scope = _lightweight.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OpsDbContext>();
        (await context.Records.CountAsync(x => x.Name == "stored", TestContext.Current.CancellationToken))
            .ShouldBe(1000);
    }
}

public record ArchiveViaOp(Guid Id);

public record PurgeViaOp(Guid Id);

public record BumpTallyViaOp(Guid Id);

public record ArchiveAndCollide(Guid Id);

public record ImportBatch(int Count);

public record StoreManyRecords(Guid[] Ids);

public record CallTheDatabaseDirectly(Guid Id);

public class EfCoreOpHandlers
{
    #region sample_returning_efcore_ops

    public static EfCoreOp Handle(ArchiveViaOp command)
    {
        return EfCoreOps.ExecuteUpdate<OpsRecord>(x => x.Id == command.Id,
            setters => setters.SetProperty(x => x.Archived, true));
    }

    public static EfCoreOp Handle(PurgeViaOp command)
    {
        return EfCoreOps.ExecuteDelete<OpsRecord>(x => x.Id == command.Id);
    }

    public static EfCoreOp Handle(BumpTallyViaOp command)
    {
        return EfCoreOps.ExecuteSql($"update ops_records set tally = tally + 1 where id = {command.Id}");
    }

    #endregion

    public static (EfCoreOp, Insert<OpsRecord>) Handle(ArchiveAndCollide command)
    {
        return (
            EfCoreOps.ExecuteUpdate<OpsRecord>(x => x.Id == command.Id,
                setters => setters.SetProperty(x => x.Archived, true)),
            // Same primary key as the row the update just touched, so SaveChangesAsync fails
            Storage.Insert(new OpsRecord { Id = command.Id, Name = "collision" }));
    }

    public static InsertManyOp<OpsRecord> Handle(ImportBatch command)
    {
        return EfCoreOps.InsertMany(Enumerable.Range(0, command.Count)
            .Select(i => new OpsRecord { Id = Guid.NewGuid(), Name = "imported", Tally = i }));
    }

    public static UnitOfWork<OpsRecord> Handle(StoreManyRecords command)
    {
        var uow = new UnitOfWork<OpsRecord>();
        foreach (var id in command.Ids)
        {
            uow.Store(new OpsRecord { Id = id, Name = "stored" });
        }

        return uow;
    }

    #region sample_requires_eager_transaction

    [RequiresEagerTransaction]
    public static async Task Handle(CallTheDatabaseDirectly command, OpsDbContext db, CancellationToken token)
    {
        await db.Records.Where(x => x.Id == command.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Archived, true), token);
    }

    #endregion
}
