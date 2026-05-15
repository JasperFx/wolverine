using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime.Handlers;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace EfCoreTests;

[Collection("sqlserver")]
public class Optimistic_concurrency_with_ef_core
{
    private readonly ITestOutputHelper _output;

    public Optimistic_concurrency_with_ef_core(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task detect_concurrency_exception_as_SagaConcurrencyException()
    {
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opt =>
                {
                    opt.DisableConventionalDiscovery().IncludeType(typeof(ConcurrencyTestSaga));

                    opt.Services.AddDbContextWithWolverineIntegration<OptConcurrencyDbContext>(o =>
                    {
                        o.UseSqlServer(Servers.SqlServerConnectionString);
                    });

                    opt.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "opt_concurrency");
                    opt.UseEntityFrameworkCoreTransactions();
                    opt.UseEntityFrameworkCoreWolverineManagedMigrations();
                    opt.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                    opt.Policies.UseDurableLocalQueues();
                    opt.Policies.AutoApplyTransactions();
                }).StartAsync();

            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OptConcurrencyDbContext>();

            // The saga's Id and the message's Id must match — Wolverine looks up the
            // saga by the message's correlation Id (the `Id` field on
            // UpdateConcurrencyTestSaga). Without a matching row the load throws
            // UnknownSagaException before the handler can fake a concurrent update,
            // which is what was making this test unconditionally fail (it was tagged
            // [Flaky] but the failure was deterministic, not racy). With matching
            // ids the saga loads, the handler's `OriginalValue = 999` trick simulates
            // a stale read, SaveChangesAsync raises DbUpdateConcurrencyException, and
            // EFCorePersistenceFrameProvider.WrapSagaConcurrencyException rethrows it
            // as SagaConcurrencyException.
            var sagaId = Guid.NewGuid();
            await dbContext.ConcurrencyTestSagas.AddAsync(new()
            {
                Id = sagaId,
                Value = "initial value",
                Version = 0,
            });
            await dbContext.SaveChangesAsync();

            await Should.ThrowAsync<SagaConcurrencyException>(() =>
                host.InvokeMessageAndWaitAsync(new UpdateConcurrencyTestSaga(sagaId, "updated value")));
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        }
    }
}

public class OptConcurrencyDbContext : DbContext
{
    public OptConcurrencyDbContext(DbContextOptions<OptConcurrencyDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConcurrencyTestSaga>()
            .Property("Version")
            .IsConcurrencyToken();
    }

    public DbSet<ConcurrencyTestSaga> ConcurrencyTestSagas { get; set; }
}

public record UpdateConcurrencyTestSaga(Guid Id, string NewValue);

public class ConcurrencyTestSaga : Saga
{
    public Guid Id { get; set; }
    public string Value { get; set; } = null!;
    public void Handle(UpdateConcurrencyTestSaga order, OptConcurrencyDbContext ctx)
    {
        // Fake 999 updates of the saga while this event is being handled. Literal must
        // be `long` so the boxed value unboxes cleanly inside EF Core's concurrency-token
        // comparator now that Saga.Version is `long` (was `int` pre-Marten-9).
        ctx.ConcurrencyTestSagas.Entry(this).Property("Version").OriginalValue = 999L;

        Value = order.NewValue;
    }
}
