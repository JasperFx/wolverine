using EfCoreTests.MultiTenancy;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.Attributes;
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
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opt =>
            {
                opt.Services.AddDbContextWithWolverineIntegration<OptConcurrencyDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opt.Services.AddScoped<IOrderRepository, OrderRepository>();

                opt.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opt.UseEntityFrameworkCoreTransactions();
                opt.Policies.UseDurableLocalQueues();
                opt.Policies.AutoApplyTransactions();
            }).StartAsync();

        var table = new Table("ConcurrencyTestSagas");
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<string>("value");
        table.AddColumn<int>("version");
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, table);
        await new SqlServerMigrator().ApplyAllAsync(conn, migration, AutoCreate.All);

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OptConcurrencyDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        await conn.CloseAsync();

        await dbContext.ConcurrencyTestSagas.AddAsync(new()
        {
            Id = Guid.NewGuid(),
            Value = "initial value",
            Version = 0,
        });
        await dbContext.SaveChangesAsync();

        Should.ThrowAsync<SagaConcurrencyException>(() => host.InvokeMessageAndWaitAsync(new UpdateConcurrencyTestSaga(Guid.NewGuid(), "updated value")));
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
    public string Value { get; set; }
    public void Handle(UpdateConcurrencyTestSaga order, OptConcurrencyDbContext ctx)
    {
        // Fake 999 updates of the saga while this event is being handled
        ctx.ConcurrencyTestSagas.Entry(this).Property("Version").OriginalValue = 999;

        Value = order.NewValue;
    }
}
