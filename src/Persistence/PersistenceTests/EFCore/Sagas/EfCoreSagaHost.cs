﻿using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using TestingSupport;
using TestingSupport.Sagas;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace PersistenceTests.EFCore.Sagas;

public class EfCoreSagaHost : ISagaHost
{
    private IHost _host;

    public IHost BuildHost<TSaga>()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.Handlers.DisableConventionalDiscovery().IncludeType<TSaga>();

            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);

            opts.Services.AddDbContext<SagaDbContext>(x => x.UseSqlServer(Servers.SqlServerConnectionString));

            opts.UseEntityFrameworkCoreTransactions();

            opts.PublishAllMessages().Locally();
        });

        // Watch if this hangs, might have to get fancier
        Initialize().GetAwaiter().GetResult();

        return _host;
    }

    public Task<T> LoadState<T>(Guid id) where T : class
    {
        var session = _host.Get<SagaDbContext>();
        return session.FindAsync<T>(id).AsTask();
    }

    public Task<T> LoadState<T>(int id) where T : class
    {
        var session = _host.Get<SagaDbContext>();
        return session.FindAsync<T>(id).AsTask();
    }

    public Task<T> LoadState<T>(long id) where T : class
    {
        var session = _host.Get<SagaDbContext>();
        return session.FindAsync<T>(id).AsTask();
    }

    public Task<T> LoadState<T>(string id) where T : class
    {
        var session = _host.Get<SagaDbContext>();
        return session.FindAsync<T>(id).AsTask();
    }

    public async Task Initialize()
    {
        var tables = new ISchemaObject[]
        {
            new WorkflowStateTable<Guid>("GuidWorkflowState"),
            new WorkflowStateTable<int>("IntWorkflowState"),
            new WorkflowStateTable<long>("LongWorkflowState"),
            new WorkflowStateTable<string>("StringWorkflowState")
        };

        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var migration = await SchemaMigration.Determine(conn, tables);
        await new SqlServerMigrator().ApplyAll(conn, migration, AutoCreate.All);

        await _host.ResetResourceState();
    }
}