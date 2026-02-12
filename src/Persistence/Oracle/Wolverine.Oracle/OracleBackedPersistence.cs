using System.Data.Common;
using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Oracle;
using Wolverine.ErrorHandling;
using Wolverine.Oracle.Transport;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Oracle;

public interface IOracleBackedPersistence
{
    /// <summary>
    /// Tell Wolverine that the persistence service (EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this Oracle database
    /// </summary>
    IOracleBackedPersistence Enroll(Type type);

    /// <summary>
    /// Tell Wolverine that the persistence service (EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this Oracle database
    /// </summary>
    IOracleBackedPersistence Enroll<T>();

    /// <summary>
    /// By default, Wolverine takes the AutoCreate settings from JasperFxOptions, but
    /// you can override the application default for just the Oracle backed
    /// envelope storage tables
    /// </summary>
    IOracleBackedPersistence OverrideAutoCreateResources(AutoCreate autoCreate);

    /// <summary>
    /// Override the database schema name for the envelope storage tables (the transactional inbox/outbox).
    /// Default is "WOLVERINE"
    /// </summary>
    IOracleBackedPersistence SchemaName(string schemaName);

    /// <summary>
    /// Override the database advisory lock number that Wolverine uses to grant temporary, exclusive
    /// access to execute scheduled messages for this application.
    /// </summary>
    IOracleBackedPersistence OverrideScheduledJobLockId(int lockId);

    /// <summary>
    /// Should Wolverine provision Oracle command queues for this Wolverine application? The default is true,
    /// but these queues are unnecessary if using an external broker for Wolverine command queues
    /// </summary>
    IOracleBackedPersistence EnableCommandQueues(bool enabled);

    /// <summary>
    /// Opt into using static per-tenant database multi-tenancy. With this option, Wolverine is assuming that
    /// there the number of tenant databases is static and does not change at runtime
    /// </summary>
    IOracleBackedPersistence RegisterStaticTenants(Action<StaticConnectionStringSource> configure);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using your own strategy for finding the right connection string
    /// for a given tenant id
    /// </summary>
    IOracleBackedPersistence RegisterTenants(ITenantedSource<string> tenantSource);

    /// <summary>
    /// Opt into multi-tenancy with separate databases using a master table lookup of tenant id to connection string
    /// that is controlled by Wolverine. This supports dynamic addition of new tenant databases at runtime without any
    /// downtime
    /// </summary>
    IOracleBackedPersistence UseMasterTableTenancy(Action<StaticConnectionStringSource> configure);

    /// <summary>
    /// Enable the Oracle messaging transport for this Wolverine application
    /// </summary>
    IOracleBackedPersistence EnableMessageTransport(Action<OraclePersistenceExpression>? configure = null);
}

/// <summary>
///     Activates the Oracle backed message persistence
/// </summary>
internal class OracleBackedPersistence : IOracleBackedPersistence, IWolverineExtension
{
    private readonly WolverineOptions _options;

    public OracleBackedPersistence(DurabilitySettings settings, WolverineOptions options)
    {
        _options = options;
        EnvelopeStorageSchemaName = settings.MessageStorageSchemaName ?? "WOLVERINE";
    }

    internal bool AlreadyIncluded { get; set; }

    public string? ConnectionString { get; set; }

    public string EnvelopeStorageSchemaName { get; set; }

    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;

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
        if (ConnectionString.IsEmpty())
        {
            throw new InvalidOperationException(
                "The Oracle backed persistence needs to have a connection string defined for the main envelope database");
        }

        // Handle duplicate key errors (ORA-00001)
        options.OnException<OracleException>(oracle =>
                oracle.Number == 1)
            .Discard();

        options.Services.AddSingleton(buildMainDatabaseSettings());
        options.CodeGeneration.AddPersistenceStrategy<LightweightSagaPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());
        options.CodeGeneration.Sources.Add(new SagaStorageVariableSource());

        options.Services.AddSingleton<IMessageStore>(s => BuildMessageStore(s.GetRequiredService<IWolverineRuntime>()));

        options.Services.AddSingleton<IDatabaseSource, MessageDatabaseDiscovery>();

        options.Services.AddSingleton<Migrator, OracleMigrator>();
    }

    public IMessageStore BuildMessageStore(IWolverineRuntime runtime)
    {
        var settings = buildMainDatabaseSettings();

        var sagaTables = runtime.Services.GetServices<SagaTableDefinition>().ToArray();

        var dataSource = new OracleDataSource(ConnectionString!);
        var logger = runtime.LoggerFactory.CreateLogger<OracleMessageStore>();

        if (UseMasterTableTenancy)
        {
            var defaultStore = new OracleMessageStore(settings, runtime.DurabilitySettings, dataSource,
                logger, sagaTables);

            ConnectionStringTenancy = new MasterTenantSource(defaultStore, runtime.Options);

            return new MultiTenantedMessageStore(defaultStore, runtime,
                new OracleTenantedMessageStore(runtime, this, sagaTables));
        }

        if (ConnectionStringTenancy != null)
        {
            var defaultStore = new OracleMessageStore(settings, runtime.DurabilitySettings, dataSource,
                logger, sagaTables);

            return new MultiTenantedMessageStore(defaultStore, runtime,
                new OracleTenantedMessageStore(runtime, this, sagaTables));
        }

        settings.Role = Role;

        return new OracleMessageStore(settings, runtime.DurabilitySettings, dataSource,
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
            ScheduledJobLockId = ScheduledJobLockId,
            SchemaName = EnvelopeStorageSchemaName,
            AddTenantLookupTable = UseMasterTableTenancy,
            TenantConnections = TenantConnections
        };
        return settings;
    }

    public IOracleBackedPersistence Enroll(Type type)
    {
        _options.Services.AddSingleton<AncillaryMessageStore>(s =>
            new AncillaryMessageStore(type, BuildMessageStore(s.GetRequiredService<IWolverineRuntime>())));

        return this;
    }

    public IOracleBackedPersistence Enroll<T>()
    {
        return Enroll(typeof(T));
    }

    IOracleBackedPersistence IOracleBackedPersistence.OverrideAutoCreateResources(AutoCreate autoCreate)
    {
        AutoCreate = autoCreate;
        return this;
    }

    IOracleBackedPersistence IOracleBackedPersistence.SchemaName(string schemaName)
    {
        if (schemaName.IsEmpty())
            throw new ArgumentNullException(nameof(schemaName), "Schema Name cannot be empty or null");

        EnvelopeStorageSchemaName = schemaName.ToUpperInvariant();
        return this;
    }

    IOracleBackedPersistence IOracleBackedPersistence.OverrideScheduledJobLockId(int lockId)
    {
        _scheduledJobLockId = lockId;
        return this;
    }

    IOracleBackedPersistence IOracleBackedPersistence.EnableCommandQueues(bool enabled)
    {
        CommandQueuesEnabled = enabled;
        return this;
    }

    IOracleBackedPersistence IOracleBackedPersistence.RegisterStaticTenants(Action<StaticConnectionStringSource> configure)
    {
        var source = new StaticConnectionStringSource();
        configure(source);
        ConnectionStringTenancy = source;

        return this;
    }

    public ITenantedSource<string>? ConnectionStringTenancy { get; set; }

    public bool UseMasterTableTenancy { get; set; }

    IOracleBackedPersistence IOracleBackedPersistence.RegisterTenants(ITenantedSource<string> tenantSource)
    {
        ConnectionStringTenancy = tenantSource;
        return this;
    }

    IOracleBackedPersistence IOracleBackedPersistence.UseMasterTableTenancy(
        Action<StaticConnectionStringSource> configure)
    {
        UseMasterTableTenancy = true;
        var source = new StaticConnectionStringSource();
        configure(source);

        TenantConnections = source;
        return this;
    }

    public StaticConnectionStringSource? TenantConnections { get; set; }

    private List<Action<OraclePersistenceExpression>> _transportConfigurations = new();

    public IOracleBackedPersistence EnableMessageTransport(Action<OraclePersistenceExpression>? configure = null)
    {
        if (configure != null)
        {
            if (AlreadyIncluded)
            {
                var transport = _options.Transports.GetOrCreate<OracleTransport>();

                var expression = new OraclePersistenceExpression(transport, _options);
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
