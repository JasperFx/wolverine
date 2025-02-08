using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using JasperFx.Resources;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;

namespace ChaosTesting;

public class MartenStorageStrategy : IMessageStorageStrategy
{
    public override string ToString()
    {
        return "Marten Persistence";
    }

    public void ConfigureReceiverPersistence(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "chaos";

            m.RegisterDocumentType<MessageRecord>();

            m.AutoCreateSchemaObjects = AutoCreate.None;
        }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "chaos_receiver");

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }

    public void ConfigureSenderPersistence(WolverineOptions opts)
    {
        opts.Policies.OnException<PostgresException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "chaos";

            m.RegisterDocumentType<MessageRecord>();

            m.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
        }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "chaos_sender");

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }

    public Task ClearMessageRecords(IServiceProvider services)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        return store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public async Task<long> FindOutstandingMessageCount(IServiceProvider container, CancellationToken cancellation)
    {
        var store = container.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        return await session.Query<MessageRecord>().CountAsync(cancellation);
    }
}

public class MultiDatabaseMartenStorageStrategy : IMessageStorageStrategy
{
    private string tenant1ConnectionString;
    private string tenant2ConnectionString;
    private string tenant3ConnectionString;

    public async Task InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        tenant1ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant1");
        tenant2ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant2");
        tenant3ConnectionString = await CreateDatabaseIfNotExists(conn, "tenant3");
    }

    private async Task<string> CreateDatabaseIfNotExists(NpgsqlConnection conn, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString);

        var exists = await conn.DatabaseExists(databaseName);
        if (!exists)
        {
            await new DatabaseSpecification().BuildDatabase(conn, databaseName);
        }

        builder.Database = databaseName;

        return builder.ConnectionString;
    }

    public override string ToString()
    {
        return "Marten Persistence";
    }

    public Task ClearMessageRecords(IServiceProvider services)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        return store.Advanced.Clean.DeleteAllDocumentsAsync();
    }

    public async Task<long> FindOutstandingMessageCount(IServiceProvider container, CancellationToken cancellation)
    {
        var store = container.GetRequiredService<IDocumentStore>();

        long count = 0;

        foreach (var database in await store.Storage.AllDatabases())
        {
            await using var session = store.OpenSession(SessionOptions.ForDatabase(database));
            count += await session.Query<MessageRecord>().CountAsync(cancellation);
        }

        return count;
    }

    public void ConfigureReceiverPersistence(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddSingleTenantDatabase(tenant1ConnectionString, "tenant1");
                tenancy.AddSingleTenantDatabase(tenant2ConnectionString, "tenant2");
                tenancy.AddSingleTenantDatabase(tenant3ConnectionString, "tenant3");
            });

            m.DatabaseSchemaName = "chaos";

            m.RegisterDocumentType<MessageRecord>();

            m.AutoCreateSchemaObjects = AutoCreate.None;
        })
        .IntegrateWithWolverine(x =>
        {
            x.MessageStorageSchemaName = "chaos_receiver";
            x.MasterDatabaseConnectionString = Servers.PostgresConnectionString;
        });

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }

    public void ConfigureSenderPersistence(WolverineOptions opts)
    {
        opts.Policies.OnException<PostgresException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

        opts.Services.AddMarten(m =>
        {
            m.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddSingleTenantDatabase(tenant1ConnectionString, "tenant1");
                tenancy.AddSingleTenantDatabase(tenant2ConnectionString, "tenant2");
                tenancy.AddSingleTenantDatabase(tenant3ConnectionString, "tenant3");
            });

            m.DatabaseSchemaName = "chaos";

            m.RegisterDocumentType<MessageRecord>();

            m.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
        })
        .IntegrateWithWolverine(x =>
        {
            x.MessageStorageSchemaName = "chaos_sender";
            x.MasterDatabaseConnectionString = "Servers.PostgresConnectionString";
        });

        opts.Services.AddResourceSetupOnStartup();

        opts.Policies.AutoApplyTransactions();

        opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();
    }
}

public class MartenMessageRecordRepository : IMessageRecordRepository
{
    private readonly IDocumentSession _session;

    public MartenMessageRecordRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<long> FindOutstandingMessageCount(CancellationToken token)
    {
        long count = 0;
        var store = _session.DocumentStore;
        foreach (var database in await store.Storage.AllDatabases())
        {
            using var session = store.OpenSession(SessionOptions.ForDatabase(database));
            count += await session.Query<MessageRecord>().CountAsync(token);
        }

        return count;
    }

    public void MarkNew(MessageRecord record)
    {
        _session.Store(record);
    }

    public ValueTask MarkDeleted(Guid id)
    {
        _session.Delete<MessageRecord>(id);
        return new ValueTask();
    }

    public Task ClearMessageRecords()
    {
        return _session.DocumentStore.Advanced.Clean.DeleteAllDocumentsAsync();
    }
}
