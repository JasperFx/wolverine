using System.Data.Common;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.MultiTenancy;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql.Transport;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Postgresql;

public interface IPostgresqlBackedPersistence
{
    /// <summary>
    /// Enable and configure the PostgreSQL backed messaging transport
    /// </summary>
    /// <param name="configure">Optional configuration of the PostgreSQL backed messaging transport</param>
    /// <returns></returns>
    IPostgresqlBackedPersistence EnableMessageTransport(Action<PostgresqlPersistenceExpression>? configure = null);

    /// <summary>
    /// By default, Wolverine takes the AutoCreate settings from JasperFxOptions, but
    /// you can override the application default for just the PostgreSQL backed queues
    /// and envelope storage tables
    /// </summary>
    /// <param name="autoCreate"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence OverrideAutoCreateResources(AutoCreate autoCreate);

    /// <summary>
    /// Override the database schema name for the envelope storage tables (the transactional inbox/outbox).
    /// Default is "wolverine"
    /// </summary>
    /// <param name="schemaName"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence SchemaName(string schemaName);

    /// <summary>
    /// Override the database advisory lock number that Wolverine uses to grant temporary, exclusive
    /// access to execute scheduled messages for this application. This is normally done by using a deterministic
    /// hash of the Wolverine envelope schema name, but you *might* need to disambiguate different applications
    /// accessing the exact same PostgreSQL database
    /// </summary>
    /// <param name="lockId"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence OverrideScheduledJobLockId(int lockId);

    /// <summary>
    /// Should Wolverine provision PostgreSQL command queues for this Wolverine application? The default is true,
    /// but these queues are unnecessary if using an external broker for Wolverine command queues -- and the Wolverine team does recommend
    /// using external brokers for command queues when that's possible
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence EnableCommandQueues(bool enabled);

    /// <summary>
    /// Opt into using static per-tenant database multi-tenancy. With this option, Wolverine is assuming that
    /// there the number of tenant databases is static and does not change at runtime
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence RegisterStaticTenants(Action<StaticConnectionStringSource> configure);
    
    /// <summary>
    /// Opt into using static per-tenant database multi-tenancy with NpgsqlDataSource. With this option, Wolverine is assuming that
    /// there the number of tenant databases is static and does not change at runtime
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence RegisterStaticTenants(Action<StaticTenantSource<NpgsqlDataSource>> configure);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using your own strategy for finding the right connection string
    /// for a given tenant id
    /// </summary>
    /// <param name="tenantSource"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence RegisterTenants(ITenantedSource<string> tenantSource);
    
    /// <summary>
    /// Opt into multi-tenancy with separate databases using your own strategy for finding the right connection string
    /// for a given tenant id
    /// </summary>
    /// <param name="tenantSource"></param>
    /// <returns></returns>
    IPostgresqlBackedPersistence RegisterTenants(ITenantedSource<NpgsqlDataSource> tenantSource);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using a master table lookup of tenant id to connection string
    /// that is controlled by Wolverine. This supports dynamic addition of new tenant databases at runtime without any
    /// downtime
    /// </summary>
    /// <returns></returns>
    IPostgresqlBackedPersistence UseMasterTableTenancy();
}

/// <summary>
///     Activates the Postgresql backed message persistence
/// </summary>
internal class PostgresqlBackedPersistence : IPostgresqlBackedPersistence, IWolverineExtension
{
    // Gotta have one or the other. Maybe even just DbDataSource here
    public NpgsqlDataSource? DataSource { get; set; }
    public string? ConnectionString { get; set; }
    
    public string EnvelopeStorageSchemaName { get; set; } = "wolverine";
    
    // This needs to be an override, and we use JasperFxOptions first!
    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;
    
    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    private int _scheduledJobLockId = 0;
    
    // This would be an override
    public int ScheduledJobLockId
    {
        get
        {
            if (_scheduledJobLockId > 0) return _scheduledJobLockId;

            return $"{EnvelopeStorageSchemaName}:scheduled-jobs".GetDeterministicHashCode();
        }
        set
        {
            _scheduledJobLockId = value;
        }
    }
    

    public void Configure(WolverineOptions options)
    {
        if (ConnectionString.IsEmpty() && DataSource == null)
        {
            throw new InvalidOperationException(
                "The PostgreSQL backed persistence needs to at least have either a connection string or NpgsqlDataSource defined for the main envelope database");
        }
        
        options.CodeGeneration.Sources.Add(new NpgsqlConnectionSource());
        options.CodeGeneration.AddPersistenceStrategy<PostgresqlPersistenceFrameProvider>();
        options.Services.AddSingleton<IDatabaseSagaStorage>(s => (IDatabaseSagaStorage)s.GetRequiredService<IMessageStore>());
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());

        options.Services.AddSingleton<IMessageStore>(s => BuildMessageStore(s.GetRequiredService<IWolverineRuntime>()));

        options.Services.AddSingleton<IDatabaseSource, MessageDatabaseDiscovery>();
        
        if (_transportConfigurations.Any())
        {
            var transport = options.Transports.GetOrCreate<PostgresqlTransport>();

            var expression = new PostgresqlPersistenceExpression(transport, options);
            foreach (var transportConfiguration in _transportConfigurations)
            {
                transportConfiguration(expression);
            }
        }
    }

    public IMessageStore BuildMessageStore(IWolverineRuntime runtime)
    {
        var settings = buildMainDatabaseSettings();

        var sagaTables = runtime.Services.GetServices<SagaTableDefinition>().ToArray();
        
        var mainSource = DataSource ?? NpgsqlDataSource.Create(ConnectionString);
        var logger = runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>();
        
        var defaultStore = new PostgresqlMessageStore(settings, runtime.DurabilitySettings, mainSource,
            logger, sagaTables);
        
        if (ConnectionStringTenancy != null || DataSourceTenancy != null)
        {


            return new MultiTenantedMessageStore(defaultStore, runtime,
                new PostgresqlTenantedMessageStore(runtime, this, sagaTables));
        }

        return defaultStore;
    }

    private DatabaseSettings buildMainDatabaseSettings()
    {
        var settings = new DatabaseSettings
        {
            CommandQueuesEnabled = CommandQueuesEnabled,
            IsMain = true,
            ConnectionString = ConnectionString,
            DataSource = DataSource,
            ScheduledJobLockId = ScheduledJobLockId,
            SchemaName = EnvelopeStorageSchemaName
        };
        return settings;
    }

    private List<Action<PostgresqlPersistenceExpression>> _transportConfigurations = new();
    
    public IPostgresqlBackedPersistence EnableMessageTransport(Action<PostgresqlPersistenceExpression>? configure = null)
    {
        if (configure != null)
        {
            _transportConfigurations.Add(configure);
        }
        return this;
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.OverrideAutoCreateResources(AutoCreate autoCreate)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.SchemaName(string schemaName)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.OverrideScheduledJobLockId(int lockId)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.EnableCommandQueues(bool enabled)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.RegisterStaticTenants(Action<StaticConnectionStringSource> configure)
    {
        var source = new StaticConnectionStringSource();
        configure(source);
        ConnectionStringTenancy = source;

        return this;
    }

    public ITenantedSource<string>? ConnectionStringTenancy { get; set; }
    public ITenantedSource<NpgsqlDataSource>? DataSourceTenancy { get; set; }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.RegisterStaticTenants(Action<StaticTenantSource<NpgsqlDataSource>> configure)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.RegisterTenants(ITenantedSource<string> tenantSource)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.RegisterTenants(ITenantedSource<NpgsqlDataSource> tenantSource)
    {
        throw new NotImplementedException();
    }

    IPostgresqlBackedPersistence IPostgresqlBackedPersistence.UseMasterTableTenancy()
    {
        throw new NotImplementedException();
    }
}