using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.AncillaryStores;

// GH-5731 / GH-5732 / GH-5733 / GH-5735. Shared plumbing for the ancillary store tests that put the main store
// and two ancillary stores (A and B) in three SEPARATE SQL Server databases, which is what the reporter runs.
//
// Separate databases are the point. With every store in one database a handler's transaction that resolved the
// wrong store still ran its SQL on its own connection against a table of the same name, so the cross-wiring
// worked by accident and the single-database tests stayed green.

public enum StoreChoice
{
    Main,
    A,
    B
}

public class AncillaryRow
{
    public Guid Id { get; set; }
    public string Step { get; set; } = null!;
}

public abstract class AncillaryRowsDbContext : DbContext
{
    protected AncillaryRowsDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<AncillaryRow> Rows => Set<AncillaryRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AncillaryRow>(map =>
        {
            map.ToTable("ancillary_rows", AncillaryStoreHarness.AppSchema);
            map.HasKey(x => new { x.Id, x.Step });
        });
    }
}

// The main database's DbContext, not enrolled anywhere: its handlers run against the main store
public class AncMainDbContext : AncillaryRowsDbContext
{
    public AncMainDbContext(DbContextOptions<AncMainDbContext> options) : base(options)
    {
    }
}

public class AncADbContext : AncillaryRowsDbContext
{
    public AncADbContext(DbContextOptions<AncADbContext> options) : base(options)
    {
    }
}

public class AncBDbContext : AncillaryRowsDbContext
{
    public AncBDbContext(DbContextOptions<AncBDbContext> options) : base(options)
    {
    }
}

public static class AncillaryStoreHarness
{
    public const string AppSchema = "anc_app";
    public const string WolverineSchema = "anc_wolverine";

    public static readonly string DatabaseAName = $"{Servers.SqlServerDatabaseName}_anc_a";
    public static readonly string DatabaseBName = $"{Servers.SqlServerDatabaseName}_anc_b";

    public static readonly string MainConnectionString = Servers.SqlServerConnectionString;
    public static readonly string ConnectionStringA = connectionStringFor(DatabaseAName);
    public static readonly string ConnectionStringB = connectionStringFor(DatabaseBName);

    // How many times each (message id, handler) pair ran, across host restarts
    public static readonly ConcurrentDictionary<string, int> Executions = new();

    public static void Executed(Guid id, string step)
    {
        Executions.AddOrUpdate($"{id}/{step}", 1, (_, count) => count + 1);
    }

    public static int ExecutionsOf(Guid id, string step)
    {
        return Executions.TryGetValue($"{id}/{step}", out var count) ? count : 0;
    }

    private static string connectionStringFor(string database)
    {
        var builder = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    public static string ConnectionStringFor(StoreChoice store)
    {
        return store switch
        {
            StoreChoice.Main => MainConnectionString,
            StoreChoice.A => ConnectionStringA,
            _ => ConnectionStringB
        };
    }

    private static bool _databasesReady;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public static async Task EnsureDatabasesAsync()
    {
        if (_databasesReady) return;

        await _lock.WaitAsync();
        try
        {
            if (_databasesReady) return;

            await using (var conn = new SqlConnection(MainConnectionString))
            {
                await conn.OpenAsync();
                foreach (var name in new[] { DatabaseAName, DatabaseBName })
                {
                    await using var cmd = conn.CreateCommand();
                    // Names derived from our own connection string, not user input
                    cmd.CommandText = $"if db_id('{name}') is null create database [{name}]";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            foreach (var cs in new[] { MainConnectionString, ConnectionStringA, ConnectionStringB })
            {
                await using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                                   if schema_id('{AppSchema}') is null exec('create schema {AppSchema}');
                                   if object_id('{AppSchema}.ancillary_rows') is null
                                       create table {AppSchema}.ancillary_rows (Id uniqueidentifier not null, Step nvarchar(100) not null, constraint pk_ancillary_rows primary key (Id, Step));
                                   """;
                await cmd.ExecuteNonQueryAsync();
            }

            _databasesReady = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Main store (and the SQL Server transport) in the main database, ancillary stores A and B in their own
    ///     databases, each with its DbContext enrolled. <paramref name="wolverineMapped" /> maps Wolverine's envelope
    ///     tables into the DbContexts, which takes the EF Core change tracker path instead of raw ADO commands.
    /// </summary>
    public static void ConfigureStores(WolverineOptions opts, bool wolverineMapped = false)
    {
        opts.UseSqlServerPersistenceAndTransport(MainConnectionString, WolverineSchema)
            .AutoProvision();

        opts.PersistMessagesWithSqlServer(ConnectionStringA, WolverineSchema, MessageStoreRole.Ancillary)
            .Enroll<AncADbContext>();
        opts.PersistMessagesWithSqlServer(ConnectionStringB, WolverineSchema, MessageStoreRole.Ancillary)
            .Enroll<AncBDbContext>();

        if (wolverineMapped)
        {
            opts.Services.AddDbContextWithWolverineIntegration<AncMainDbContext>(
                x => x.UseSqlServer(MainConnectionString), WolverineSchema);
            opts.Services.AddDbContextWithWolverineIntegration<AncADbContext>(
                x => x.UseSqlServer(ConnectionStringA), WolverineSchema);
            opts.Services.AddDbContextWithWolverineIntegration<AncBDbContext>(
                x => x.UseSqlServer(ConnectionStringB), WolverineSchema);
        }
        else
        {
            opts.Services.AddDbContext<AncMainDbContext>(x => x.UseSqlServer(MainConnectionString));
            opts.Services.AddDbContext<AncADbContext>(x => x.UseSqlServer(ConnectionStringA));
            opts.Services.AddDbContext<AncBDbContext>(x => x.UseSqlServer(ConnectionStringB));
        }

        opts.UseEntityFrameworkCoreTransactions();
        opts.Policies.AutoApplyTransactions();

        opts.Services.AddResourceSetupOnStartup();
    }

    public static IMessageStore StoreFor(this IHost host, StoreChoice store)
    {
        var runtime = host.GetRuntime();
        return store switch
        {
            StoreChoice.Main => runtime.Storage,
            StoreChoice.A => runtime.Stores.FindAncillaryStore(typeof(AncADbContext)),
            _ => runtime.Stores.FindAncillaryStore(typeof(AncBDbContext))
        };
    }

    /// <summary>
    ///     Every inbox row for a message of <paramref name="messageType" /> that is still Incoming, in any of the three
    ///     databases. By message type, because each test resets the stores and the message's own id is not the
    ///     envelope's
    /// </summary>
    public static async Task<string[]> StrandedIncomingAsync(this IHost host, Type messageType)
    {
        var stranded = new List<string>();
        foreach (var store in Enum.GetValues<StoreChoice>())
        {
            var incoming = await host.StoreFor(store).Admin.AllIncomingAsync();
            stranded.AddRange(incoming
                .Where(x => x.MessageType == messageType.ToMessageTypeName() && x.Status == EnvelopeStatus.Incoming)
                .Select(x => $"{store}: {x.MessageType} at {x.Destination} owned by {x.OwnerId}"));
        }

        return stranded.ToArray();
    }

    public static async Task<string[]> StrandedOutgoingAsync(this IHost host, Type messageType)
    {
        var stranded = new List<string>();
        foreach (var store in Enum.GetValues<StoreChoice>())
        {
            var outgoing = await host.StoreFor(store).Admin.AllOutgoingAsync();
            stranded.AddRange(outgoing
                .Where(x => x.MessageType == messageType.ToMessageTypeName())
                .Select(x => $"{store}: {x.MessageType} to {x.Destination} owned by {x.OwnerId}"));
        }

        return stranded.ToArray();
    }

    public static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
    }

    public static async Task<bool> RowExistsAsync(StoreChoice store, Guid id, string step)
    {
        await using var conn = new SqlConnection(ConnectionStringFor(store));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {AppSchema}.ancillary_rows where Id = @id and Step = @step";
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("step", step);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }
}
