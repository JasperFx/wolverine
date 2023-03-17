using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.EFCore;

[Collection("sqlserver")]
public class Bug_252_codegen_issue
{
    [Fact]
    public async Task use_the_saga_type_to_determine_the_correct_DbContext_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opt =>
            {
                opt.Services.AddDbContextWithWolverineIntegration<AppDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opt.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opt.UseEntityFrameworkCoreTransactions();
                opt.Policies.UseDurableLocalQueues();
                opt.Policies.AutoApplyTransactions();
            }).StartAsync();

        var table = new Table("OrderSagas");
        table.AddColumn<Guid>("id").AsPrimaryKey();
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, table);
        await new SqlServerMigrator().ApplyAllAsync(conn, migration, AutoCreate.All);

        var dbContext = host.Services.GetRequiredService<AppDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        await host.InvokeMessageAndWaitAsync(new OrderCreated(Guid.NewGuid()));
    }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ProcessOrderSaga> OrderSagas { get; set; }
}

public record OrderCreated(Guid Id);

public class ProcessOrderSaga : Saga
{
    public Guid Id { get; set; }

    public void Start(OrderCreated order)
    {
        Id = order.Id;
    }
}