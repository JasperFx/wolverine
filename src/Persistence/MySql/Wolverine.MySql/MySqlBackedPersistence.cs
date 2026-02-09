using System.Data.Common;
using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.MySql;
using Wolverine.ErrorHandling;
using Wolverine.MySql.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.MySql;

public interface IMySqlBackedPersistence
{
    /// <summary>
    /// Tell Wolverine that the persistence service (EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this MySQL database
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    IMySqlBackedPersistence Enroll(Type type);

    /// <summary>
    /// Tell Wolverine that the persistence service (EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this MySQL database
    /// </summary>
    /// <returns></returns>
    IMySqlBackedPersistence Enroll<T>();

    /// <summary>
    /// By default, Wolverine takes the AutoCreate settings from JasperFxOptions, but
    /// you can override the application default for just the MySQL backed
    /// envelope storage tables
    /// </summary>
    /// <param name="autoCreate"></param>
    /// <returns></returns>
    IMySqlBackedPersistence OverrideAutoCreateResources(AutoCreate autoCreate);

    /// <summary>
    /// Override the database schema name for the envelope storage tables (the transactional inbox/outbox).
    /// Default is "wolverine"
    /// </summary>
    /// <param name="schemaName"></param>
    /// <returns></returns>
    IMySqlBackedPersistence SchemaName(string schemaName);

    /// <summary>
    /// Override the database advisory lock number that Wolverine uses to grant temporary, exclusive
    /// access to execute scheduled messages for this application.
    /// </summary>
    /// <param name="lockId"></param>
    /// <returns></returns>
    IMySqlBackedPersistence OverrideScheduledJobLockId(int lockId);

    /// <summary>
    /// Should Wolverine provision MySQL command queues for this Wolverine application? The default is true,
    /// but these queues are unnecessary if using an external broker for Wolverine command queues
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    IMySqlBackedPersistence EnableCommandQueues(bool enabled);

    /// <summary>
    /// Opt into using static per-tenant database multi-tenancy. With this option, Wolverine is assuming that
    /// there the number of tenant databases is static and does not change at runtime
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IMySqlBackedPersistence RegisterStaticTenants(Action<StaticConnectionStringSource> configure);

    /// <summary>
    /// Opt into using static per-tenant database multi-tenancy with MySqlDataSource. With this option, Wolverine is assuming that
    /// there the number of tenant databases is static and does not change at runtime
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IMySqlBackedPersistence RegisterStaticTenantsByDataSource(Action<StaticTenantSource<MySqlDataSource>> configure);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using your own strategy for finding the right connection string
    /// for a given tenant id
    /// </summary>
    /// <param name="tenantSource"></param>
    /// <returns></returns>
    IMySqlBackedPersistence RegisterTenants(ITenantedSource<string> tenantSource);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using your own strategy for finding the right connection string
    /// for a given tenant id
    /// </summary>
    /// <param name="tenantSource"></param>
    /// <returns></returns>
    IMySqlBackedPersistence RegisterTenants(ITenantedSource<MySqlDataSource> tenantSource);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using a master table lookup of tenant id to connection string
    /// that is controlled by Wolverine. This supports dynamic addition of new tenant databases at runtime without any
    /// downtime
    /// </summary>
    /// <param name="configure">Register any default tenants and connection strings to seed the table.</param>
    /// <returns></returns>
    IMySqlBackedPersistence UseMasterTableTenancy(Action<StaticConnectionStringSource> configure);

    /// <summary>
    /// Enable the MySQL messaging transport for this Wolverine application
    /// </summary>
    /// <param name="configure">Optional configuration for the transport</param>
    /// <returns></returns>
    IMySqlBackedPersistence EnableMessageTransport(Action<MySqlPersistenceExpression>? configure = null);
}

/// <summary>
///     Activates the MySQL backed message persistence
/// </summary>
internal class MySqlBackedPersistence : IMySqlBackedPersistence, IWolverineExtension
{
    private readonly WolverineOptions _options;

    public MySqlBackedPersistence(DurabilitySettings settings, WolverineOptions options)
    {
        _options = options;
        EnvelopeStorageSchemaName = settings.MessageStorageSchemaName ?? "wolverine";
    }

    internal bool AlreadyIncluded { get; set; }

    public MySqlDataSource? DataSource { get; set; }
    public string? ConnectionString { get; set; }

    public string EnvelopeStorageSchemaName { get; set; }

    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;

    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    private int _scheduledJobLockId;

    public int ScheduledJobLockId
    {
        get
        {
            if (_scheduledJobLockId > 0) return _scheduledJobLockId;

            return $"{EnvelopeStorageSchemaName}:scheduled-jobs".GetDeterministicHashCode();
        }
        set => _scheduledJobLockId = value;
    }


    public void Configure(WolverineOptions options)
    {
        if (ConnectionString.IsEmpty() && DataSource == null)
        {
            throw new InvalidOperationException(
                "The MySQL backed persistence needs to at least have either a connection string or MySqlDataSource defined for the main envelope database");
        }

        // Handle duplicate key errors
        options.OnException<MySqlException>(mysql =>
                mysql.Number == 1062) // 1062 is MySQL duplicate entry error
            .Discard();

        options.Services.AddSingleton(buildMainDatabaseSettings());
        options.CodeGeneration.AddPersistenceStrategy<LightweightSagaPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());
        options.CodeGeneration.Sources.Add(new SagaStorageVariableSource());

        options.Services.AddSingleton<IMessageStore>(s => BuildMessageStore(s.GetRequiredService<IWolverineRuntime>()));

        options.Services.AddSingleton<IDatabaseSource, MessageDatabaseDiscovery>();
        
        options.Services.AddSingleton<Migrator, MySqlMigrator>();
    }

    public IMessageStore BuildMessageStore(IWolverineRuntime runtime)
    {
        var settings = buildMainDatabaseSettings();

        var sagaTables = runtime.Services.GetServices<SagaTableDefinition>().ToArray();

        var mainSource = DataSource ?? MySqlDataSourceFactory.Create(ConnectionString!);
        var logger = runtime.LoggerFactory.CreateLogger<MySqlMessageStore>();

        if (UseMasterTableTenancy)
        {
            var defaultStore = new MySqlMessageStore(settings, runtime.DurabilitySettings, mainSource,
                logger, sagaTables);

            ConnectionStringTenancy = new MasterTenantSource(defaultStore, runtime.Options);

            return new MultiTenantedMessageStore(defaultStore, runtime,
                new MySqlTenantedMessageStore(runtime, this, sagaTables));
        }

        if (ConnectionStringTenancy != null || DataSourceTenancy != null)
        {
            var defaultStore = new MySqlMessageStore(settings, runtime.DurabilitySettings, mainSource,
                logger, sagaTables);

            return new MultiTenantedMessageStore(defaultStore, runtime,
                new MySqlTenantedMessageStore(runtime, this, sagaTables));
        }

        settings.Role = Role;

        return new MySqlMessageStore(settings, runtime.DurabilitySettings, mainSource,
            logger, sagaTables);
    }

    public MessageStoreRole Role { get; set; } = MessageStoreRole.Main;

    private DatabaseSettings buildMainDatabaseSettings()
    {
        var settings = new DatabaseSettings
        {
            CommandQueuesEnabled = CommandQueuesEnabled,
            Role = MessageStoreRole.Main,
            ConnectionString = ConnectionString,
            DataSource = DataSource,
            ScheduledJobLockId = ScheduledJobLockId,
            SchemaName = EnvelopeStorageSchemaName,
            AddTenantLookupTable = UseMasterTableTenancy,
            TenantConnections = TenantConnections
        };
        return settings;
    }

    public IMySqlBackedPersistence Enroll(Type type)
    {
        _options.Services.AddSingleton<AncillaryMessageStore>(s =>
            new AncillaryMessageStore(type, BuildMessageStore(s.GetRequiredService<IWolverineRuntime>())));

        return this;
    }

    public IMySqlBackedPersistence Enroll<T>()
    {
        return Enroll(typeof(T));
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.OverrideAutoCreateResources(AutoCreate autoCreate)
    {
        AutoCreate = autoCreate;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.SchemaName(string schemaName)
    {
        // MySQL is case-insensitive for database names on some platforms
        if (schemaName.IsEmpty())
            throw new ArgumentNullException(nameof(schemaName), "Schema Name cannot be empty or null");

        EnvelopeStorageSchemaName = schemaName;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.OverrideScheduledJobLockId(int lockId)
    {
        _scheduledJobLockId = lockId;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.EnableCommandQueues(bool enabled)
    {
        CommandQueuesEnabled = enabled;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.RegisterStaticTenants(Action<StaticConnectionStringSource> configure)
    {
        var source = new StaticConnectionStringSource();
        configure(source);
        ConnectionStringTenancy = source;

        return this;
    }

    public ITenantedSource<string>? ConnectionStringTenancy { get; set; }
    public ITenantedSource<MySqlDataSource>? DataSourceTenancy { get; set; }

    public bool UseMasterTableTenancy { get; set; }

    IMySqlBackedPersistence IMySqlBackedPersistence.RegisterStaticTenantsByDataSource(
        Action<StaticTenantSource<MySqlDataSource>> configure)
    {
        var tenants = new StaticTenantSource<MySqlDataSource>();
        configure(tenants);
        DataSourceTenancy = tenants;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.RegisterTenants(ITenantedSource<string> tenantSource)
    {
        ConnectionStringTenancy = tenantSource;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.RegisterTenants(ITenantedSource<MySqlDataSource> tenantSource)
    {
        DataSourceTenancy = tenantSource;
        return this;
    }

    IMySqlBackedPersistence IMySqlBackedPersistence.UseMasterTableTenancy(
        Action<StaticConnectionStringSource> configure)
    {
        UseMasterTableTenancy = true;
        var source = new StaticConnectionStringSource();
        configure(source);

        TenantConnections = source;
        return this;
    }

    /// <summary>
    /// This is any default connection strings by tenant that should be loaded at start up time
    /// </summary>
    public StaticConnectionStringSource? TenantConnections { get; set; }

    private List<Action<MySqlPersistenceExpression>> _transportConfigurations = new();

    public IMySqlBackedPersistence EnableMessageTransport(Action<MySqlPersistenceExpression>? configure = null)
    {
        if (configure != null)
        {
            if (AlreadyIncluded)
            {
                var transport = _options.Transports.GetOrCreate<MySqlTransport>();

                var expression = new MySqlPersistenceExpression(transport, _options);
                configure(expression);
            }
            else
            {
                _transportConfigurations.Add(configure);
            }
        }
        return this;
    }
}

internal static class MySqlDataSourceFactory
{
    public static MySqlDataSource Create(string connectionString)
    {
        return new MySqlDataSourceBuilder(connectionString).Build();
    }
}
