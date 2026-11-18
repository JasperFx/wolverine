using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests.Sagas;

public class UpgradedSaga5636 : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Touches { get; set; }

    public void Handle(TouchUpgradedSaga5636 command) => Touches++;
}

public record TouchUpgradedSaga5636(Guid UpgradedSaga5636Id);

public class UpgradedSagaDbContext5636 : DbContext
{
    public const string SchemaName = "saga_upgrade_5636";
    public const string TableName = "upgraded_sagas";

    public UpgradedSagaDbContext5636(DbContextOptions<UpgradedSagaDbContext5636> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UpgradedSaga5636>(map =>
        {
            map.ToTable(TableName, SchemaName);
            map.HasKey(x => x.Id);
        });
    }
}

// GH-5636: an EF Core saga table that Wolverine manages through Weasel (UseEntityFrameworkCoreWolverineManagedMigrations)
// and that a 6.x application created, with sagas in it, has neither Saga.LastModified (GH-5345) nor Saga.CompletedAt
// (GH-5572). Starting 7.0 against it adds both columns, the sagas already in it read back with LastModified at its
// default, and they carry on.
public abstract class saga_tables_from_6x_5636
{
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();

    protected abstract Task createThe6xTableWithSagasAsync(Guid first, Guid second, CancellationToken token);
    protected abstract void configureStorage(WolverineOptions opts);

    [Fact]
    public async Task the_7_0_host_starts_and_adds_the_new_saga_columns()
    {
        var token = TestContext.Current.CancellationToken;
        await createThe6xTableWithSagasAsync(_first, _second, token);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<UpgradedSaga5636>();
                opts.Durability.Mode = DurabilityMode.Solo;

                configureStorage(opts);

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(token);

        // The sagas 6.x wrote are still there, with the new columns at their defaults
        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<UpgradedSagaDbContext5636>();
            var sagas = await dbContext.Set<UpgradedSaga5636>().AsNoTracking().OrderBy(x => x.Name).ToListAsync(token);

            sagas.Select(x => x.Name).ShouldBe(["first", "second"]);
            sagas.ShouldAllBe(x => x.LastModified == DateTimeOffset.MinValue);
            sagas.ShouldAllBe(x => x.CompletedAt == null);
        }

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await host.InvokeMessageAndWaitAsync(new TouchUpgradedSaga5636(_first));

        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<UpgradedSagaDbContext5636>();
            var touched = await dbContext.Set<UpgradedSaga5636>().AsNoTracking().SingleAsync(x => x.Id == _first, token);

            touched.Touches.ShouldBe(4);
            touched.LastModified.ShouldBeGreaterThan(before);
        }
    }
}

public class saga_tables_from_6x_on_postgresql_5636 : saga_tables_from_6x_5636
{
    protected override async Task createThe6xTableWithSagasAsync(Guid first, Guid second, CancellationToken token)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(token);

        // The table as EF Core's model mapped it under Wolverine 6.x, with two sagas in it
        await using var command = conn.CreateCommand();
        command.CommandText = $"""
            drop schema if exists {UpgradedSagaDbContext5636.SchemaName} cascade;
            create schema {UpgradedSagaDbContext5636.SchemaName};
            create table {UpgradedSagaDbContext5636.SchemaName}.{UpgradedSagaDbContext5636.TableName} (
                "Id" uuid not null,
                "Name" text not null,
                "Touches" integer not null,
                "Version" integer not null,
                constraint "PK_{UpgradedSagaDbContext5636.TableName}" primary key ("Id")
            );
            insert into {UpgradedSagaDbContext5636.SchemaName}.{UpgradedSagaDbContext5636.TableName} ("Id", "Name", "Touches", "Version")
                values (@first, 'first', 3, 1), (@second, 'second', 0, 1);
            """;
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("second", second);
        await command.ExecuteNonQueryAsync(token);
    }

    protected override void configureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_upgrade_5636_wolverine");
        opts.Services.AddDbContextWithWolverineIntegration<UpgradedSagaDbContext5636>(
            x => x.UseNpgsql(Servers.PostgresConnectionString));
    }
}

public class saga_tables_from_6x_on_sqlserver_5636 : saga_tables_from_6x_5636
{
    protected override async Task createThe6xTableWithSagasAsync(Guid first, Guid second, CancellationToken token)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(token);

        var schema = UpgradedSagaDbContext5636.SchemaName;
        var table = UpgradedSagaDbContext5636.TableName;

        await execute(conn, $"""
            if object_id('{schema}.{table}', 'U') is not null drop table {schema}.{table};
            if schema_id('{schema}') is null exec('create schema {schema}');
            """, token);

        // The table as EF Core's model mapped it under Wolverine 6.x, with two sagas in it
        await execute(conn, $"""
            create table {schema}.{table} (
                Id uniqueidentifier not null,
                Name nvarchar(max) not null,
                Touches int not null,
                Version int not null,
                constraint PK_{table} primary key (Id)
            );
            """, token);

        await using var insert = conn.CreateCommand();
        insert.CommandText = $"insert into {schema}.{table} (Id, Name, Touches, Version) values (@first, 'first', 3, 1), (@second, 'second', 0, 1)";
        insert.Parameters.AddWithValue("first", first);
        insert.Parameters.AddWithValue("second", second);
        await insert.ExecuteNonQueryAsync(token);
    }

    private static async Task execute(SqlConnection conn, string sql, CancellationToken token)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    protected override void configureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_upgrade_5636_wolverine");
        opts.Services.AddDbContextWithWolverineIntegration<UpgradedSagaDbContext5636>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString));
    }
}
