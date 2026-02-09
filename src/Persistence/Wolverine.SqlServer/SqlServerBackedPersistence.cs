using System.Data.Common;
using JasperFx;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;
using Wolverine.SqlServer.Sagas;
using Wolverine.SqlServer.Transport;
using Wolverine.SqlServer.Util;

namespace Wolverine.SqlServer;

public interface ISqlServerBackedPersistence
{
    /// <summary>
    /// Enable and configure the PostgreSQL backed messaging transport
    /// </summary>
    /// <param name="configure">Optional configuration of the PostgreSQL backed messaging transport</param>
    /// <returns></returns>
    ISqlServerBackedPersistence EnableMessageTransport(Action<SqlServerPersistenceExpression>? configure = null);

    /// <summary>
    /// By default, Wolverine takes the AutoCreate settings from JasperFxOptions, but
    /// you can override the application default for just the PostgreSQL backed queues
    /// and envelope storage tables
    /// </summary>
    /// <param name="autoCreate"></param>
    /// <returns></returns>
    ISqlServerBackedPersistence OverrideAutoCreateResources(AutoCreate autoCreate);

    /// <summary>
    /// Override the database schema name for the envelope storage tables (the transactional inbox/outbox).
    /// Default is "wolverine"
    /// </summary>
    /// <param name="schemaName"></param>
    /// <returns></returns>
    ISqlServerBackedPersistence SchemaName(string schemaName);

    /// <summary>
    /// Override the database advisory lock number that Wolverine uses to grant temporary, exclusive
    /// access to execute scheduled messages for this application. This is normally done by using a deterministic
    /// hash of the Wolverine envelope schema name, but you *might* need to disambiguate different applications
    /// accessing the exact same PostgreSQL database
    /// </summary>
    /// <param name="lockId"></param>
    /// <returns></returns>
    ISqlServerBackedPersistence OverrideScheduledJobLockId(int lockId);

    /// <summary>
    /// Should Wolverine provision PostgreSQL command queues for this Wolverine application? The default is true,
    /// but these queues are unnecessary if using an external broker for Wolverine command queues -- and the Wolverine team does recommend
    /// using external brokers for command queues when that's possible
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    ISqlServerBackedPersistence EnableCommandQueues(bool enabled);

    /// <summary>
    /// Opt into using static per-tenant database multi-tenancy. With this option, Wolverine is assuming that
    /// there the number of tenant databases is static and does not change at runtime
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    ISqlServerBackedPersistence RegisterStaticTenants(Action<StaticConnectionStringSource> configure);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using your own strategy for finding the right connection string
    /// for a given tenant id
    /// </summary>
    /// <param name="tenantSource"></param>
    /// <returns></returns>
    ISqlServerBackedPersistence RegisterTenants(ITenantedSource<string> tenantSource);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using a master table lookup of tenant id to connection string
    /// that is controlled by Wolverine. This supports dynamic addition of new tenant databases at runtime without any
    /// downtime
    /// </summary>
    /// <param name="configure">Register any default tenants and connection strings to seed the table. This might be helpful for testing and local development</param>
    /// <returns></returns>
    ISqlServerBackedPersistence UseMasterTableTenancy(Action<StaticConnectionStringSource> configure);

    /// <summary>
    /// Tell Wolverine that the persistence service (Marten? EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this PostgreSQL database
    /// </summary>
    /// <param name="serviceType"></param>
    ISqlServerBackedPersistence Enroll(Type serviceType);
    
    /// <summary>
    /// Tell Wolverine that the persistence service (Marten? EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this PostgreSQL database
    /// </summary>
    /// <param name="serviceType"></param>
    ISqlServerBackedPersistence Enroll<T>();
}

/// <summary>
///     Activates the Sql Server backed message persistence
/// </summary>
internal class SqlServerBackedPersistence : IWolverineExtension, ISqlServerBackedPersistence
{
    private readonly WolverineOptions _options;
    private readonly List<Action<SqlServerMessageStore>> _storeConfigurations = new();

    public SqlServerBackedPersistence(WolverineOptions options)
    {
        _options = options;
    }

    internal WolverineOptions Options => _options;

    internal void AddStoreConfiguration(Action<SqlServerMessageStore> configuration)
    {
        _storeConfigurations.Add(configuration);
    }

    public string? ConnectionString { get; set; }
    
    public string EnvelopeStorageSchemaName { get; set; } = "wolverine";
    
    // This needs to be an override, and we use JasperFxOptions first!
    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;
    
    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;
    
    public bool UseMasterTableTenancy { get; set; }


    private int _scheduledJobLockId = 0;
    
    public ITenantedSource<string>? ConnectionStringTenancy { get; set; }
    
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
        if (ConnectionString.IsEmpty())
        {
            throw new InvalidOperationException(
                "The Sql Server backed persistence needs to at least have a main connection string for the main envelope database");
        }

        // This needs to stay in to help w/ EF Core customization
        options.Services.AddSingleton(buildMainDatabaseSettings());
        options.CodeGeneration.AddPersistenceStrategy<LightweightSagaPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());
        options.CodeGeneration.Sources.Add(new SagaStorageVariableSource());

        options.Services.AddSingleton<Migrator, SqlServerMigrator>();
        
        options.Services.AddSingleton<IMessageStore>(s => BuildMessageStore(s.GetRequiredService<IWolverineRuntime>()));

        options.Services.AddSingleton<IDatabaseSource, MessageDatabaseDiscovery>();
        
        if (_transportConfigurations.Any())
        {
            // var transport = options.Transports.GetOrCreate<SqlServerTransport>();
            //
            // var expression = new SqlServerPersistenceExpression(transport, options);
            // foreach (var transportConfiguration in _transportConfigurations)
            // {
            //     transportConfiguration(expression);
            // }
        }
    }
    
    public IMessageStore BuildMessageStore(IWolverineRuntime runtime)
    {
        var settings = buildMainDatabaseSettings();

        var sagaTables = runtime.Services.GetServices<SagaTableDefinition>().ToArray();
        
        var logger = runtime.LoggerFactory.CreateLogger<SqlServerMessageStore>();
        
        if (UseMasterTableTenancy)
        {
            var defaultStore = new SqlServerMessageStore(settings, runtime.DurabilitySettings,
                logger, sagaTables);
            applyStoreConfigurations(defaultStore);
            
            ConnectionStringTenancy = new MasterTenantSource(defaultStore, runtime.Options);
            
            return new MultiTenantedMessageStore(defaultStore, runtime,
                new SqlServerTenantedMessageStore(runtime, this, sagaTables){DataSource = ConnectionStringTenancy});
        }

        if (ConnectionStringTenancy != null)
        {
            var defaultStore = new SqlServerMessageStore(settings, runtime.DurabilitySettings,
                logger, sagaTables);
            applyStoreConfigurations(defaultStore);
            
            return new MultiTenantedMessageStore(defaultStore, runtime,
                new SqlServerTenantedMessageStore(runtime, this, sagaTables){DataSource = ConnectionStringTenancy});
        }

        settings.Role = Role;
        
        var store = new SqlServerMessageStore(settings, runtime.DurabilitySettings,
            logger, sagaTables);
        applyStoreConfigurations(store);
        return store;
    }

    internal void ApplyStoreConfigurations(SqlServerMessageStore store)
    {
        applyStoreConfigurations(store);
    }

    private void applyStoreConfigurations(SqlServerMessageStore store)
    {
        foreach (var configuration in _storeConfigurations)
        {
            configuration(store);
        }
    }

    private DatabaseSettings buildMainDatabaseSettings()
    {
        return new DatabaseSettings
        {
            CommandQueuesEnabled = CommandQueuesEnabled,
            Role = MessageStoreRole.Main,
            ConnectionString = ConnectionString,
            ScheduledJobLockId = ScheduledJobLockId,
            SchemaName = EnvelopeStorageSchemaName,
            AddTenantLookupTable = UseMasterTableTenancy,
            TenantConnections = TenantConnections
        };
    }

    private List<Action<SqlServerPersistenceExpression>> _transportConfigurations = new();


    ISqlServerBackedPersistence ISqlServerBackedPersistence.EnableMessageTransport(Action<SqlServerPersistenceExpression>? configure)
    {
        throw new NotImplementedException();
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.OverrideAutoCreateResources(AutoCreate autoCreate)
    {
        AutoCreate = autoCreate;
        return this;
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.SchemaName(string schemaName)
    {
        EnvelopeStorageSchemaName = schemaName;
        return this;
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.OverrideScheduledJobLockId(int lockId)
    {
        _scheduledJobLockId = lockId;
        return this;
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.EnableCommandQueues(bool enabled)
    {
        CommandQueuesEnabled = enabled;
        return this;
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.RegisterStaticTenants(Action<StaticConnectionStringSource> configure)
    {
        var source = new StaticConnectionStringSource();
        configure(source);
        ConnectionStringTenancy = source;

        return this;
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.RegisterTenants(ITenantedSource<string> tenantSource)
    {
        ConnectionStringTenancy = tenantSource;
        return this;
    }

    ISqlServerBackedPersistence ISqlServerBackedPersistence.UseMasterTableTenancy(Action<StaticConnectionStringSource> configure)
    {
        UseMasterTableTenancy = true;
        var source = new StaticConnectionStringSource();
        configure(source);

        TenantConnections = source;
        return this;
    }

    public ISqlServerBackedPersistence Enroll(Type serviceType)
    {
        _options.Services.AddSingleton<AncillaryMessageStore>(s => new (serviceType,BuildMessageStore(s.GetRequiredService<IWolverineRuntime>())));
        return this;
    }

    public ISqlServerBackedPersistence Enroll<T>()
    {
        return Enroll(typeof(T));
    }

    /// <summary>
    /// This is any default connection strings by tenant that should be loaded at start up time
    /// </summary>
    public StaticConnectionStringSource? TenantConnections { get; set; }

    public MessageStoreRole Role { get; set; } = MessageStoreRole.Main;
}