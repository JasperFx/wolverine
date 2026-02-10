using System.Data.Common;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Weasel.Core.Migrations;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.Sqlite.Transport;

namespace Wolverine.Sqlite;

public interface ISqliteBackedPersistence
{
    /// <summary>
    /// Enable and configure the SQLite backed messaging transport
    /// </summary>
    /// <param name="configure">Optional configuration of the SQLite backed messaging transport</param>
    /// <returns></returns>
    ISqliteBackedPersistence EnableMessageTransport(Action<SqlitePersistenceExpression>? configure = null);

    /// <summary>
    /// Tell Wolverine that the persistence service (EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this SQLite database
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    ISqliteBackedPersistence Enroll(Type type);

    /// <summary>
    /// Tell Wolverine that the persistence service (EF Core DbContext? Something else?) of the given
    /// type should be enrolled in envelope storage with this SQLite database
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    ISqliteBackedPersistence Enroll<T>();

    /// <summary>
    /// By default, Wolverine takes the AutoCreate settings from JasperFxOptions, but
    /// you can override the application default for just the SQLite backed queues
    /// and envelope storage tables
    /// </summary>
    /// <param name="autoCreate"></param>
    /// <returns></returns>
    ISqliteBackedPersistence OverrideAutoCreateResources(AutoCreate autoCreate);

    /// <summary>
    /// Override the database schema name for the envelope storage tables (the transactional inbox/outbox).
    /// Default is "wolverine"
    /// </summary>
    /// <param name="schemaName"></param>
    /// <returns></returns>
    ISqliteBackedPersistence SchemaName(string schemaName);

    /// <summary>
    /// Override the database advisory lock number that Wolverine uses to grant temporary, exclusive
    /// access to execute scheduled messages for this application. This is normally done by using a deterministic
    /// hash of the Wolverine envelope schema name
    /// </summary>
    /// <param name="lockId"></param>
    /// <returns></returns>
    ISqliteBackedPersistence OverrideScheduledJobLockId(int lockId);

    /// <summary>
    /// Should Wolverine provision SQLite command queues for this Wolverine application? The default is true,
    /// but these queues are unnecessary if using an external broker for Wolverine command queues
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    ISqliteBackedPersistence EnableCommandQueues(bool enabled);
}

/// <summary>
///     Activates the SQLite backed message persistence
/// </summary>
internal class SqliteBackedPersistence : ISqliteBackedPersistence, IWolverineExtension
{
    private readonly WolverineOptions _options;

    public SqliteBackedPersistence(DurabilitySettings settings, WolverineOptions options)
    {
        _options = options;
        EnvelopeStorageSchemaName = settings.MessageStorageSchemaName ?? "main";
    }

    internal bool AlreadyIncluded { get; set; }

    public DbDataSource? DataSource { get; set; }
    public string? ConnectionString { get; set; }

    public string EnvelopeStorageSchemaName { get; set; }

    public AutoCreate AutoCreate { get; set; } = JasperFx.AutoCreate.CreateOrUpdate;

    /// <summary>
    ///     Is this database exposing command queues?
    /// </summary>
    public bool CommandQueuesEnabled { get; set; } = true;

    private int _scheduledJobLockId = 0;

    public int ScheduledJobLockId
    {
        get
        {
            if (_scheduledJobLockId > 0) return _scheduledJobLockId;

            return $"{EnvelopeStorageSchemaName}:scheduled-jobs".GetDeterministicHashCode();
        }
        set { _scheduledJobLockId = value; }
    }

    public void Configure(WolverineOptions options)
    {
        if (ConnectionString.IsEmpty() && DataSource == null)
        {
            throw new InvalidOperationException(
                "The SQLite backed persistence needs to at least have either a connection string or DbDataSource defined for the main envelope database");
        }

        options.Services.AddSingleton(buildMainDatabaseSettings());
        options.CodeGeneration.AddPersistenceStrategy<LightweightSagaPersistenceFrameProvider>();
        options.CodeGeneration.Sources.Add(new DatabaseBackedPersistenceMarker());
        options.CodeGeneration.Sources.Add(new SagaStorageVariableSource());

        options.Services.AddSingleton<IMessageStore>(s => BuildMessageStore(s.GetRequiredService<IWolverineRuntime>()));

        options.Services.AddSingleton<IDatabaseSource, MessageDatabaseDiscovery>();

        if (_transportConfigurations.Any())
        {
            var transport = options.Transports.GetOrCreate<SqliteTransport>();

            var expression = new SqlitePersistenceExpression(transport, options);
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

        var mainSource = DataSource ?? new WolverineSqliteDataSource(ConnectionString!);
        var logger = runtime.LoggerFactory.CreateLogger<SqliteMessageStore>();

        settings.Role = Role;

        return new SqliteMessageStore(settings, runtime.DurabilitySettings, mainSource,
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
            SchemaName = EnvelopeStorageSchemaName
        };
        return settings;
    }

    private List<Action<SqlitePersistenceExpression>> _transportConfigurations = new();

    public ISqliteBackedPersistence EnableMessageTransport(Action<SqlitePersistenceExpression>? configure = null)
    {
        if (configure != null)
        {
            if (AlreadyIncluded)
            {
                var transport = _options.Transports.GetOrCreate<SqliteTransport>();

                var expression = new SqlitePersistenceExpression(transport, _options);
                configure(expression);
            }
            else
            {
                _transportConfigurations.Add(configure);
            }
        }
        return this;
    }

    public ISqliteBackedPersistence Enroll(Type type)
    {
        _options.Services.AddSingleton<AncillaryMessageStore>(s => new(type, BuildMessageStore(s.GetRequiredService<IWolverineRuntime>())));

        return this;
    }

    public ISqliteBackedPersistence Enroll<T>()
    {
        return Enroll(typeof(T));
    }

    ISqliteBackedPersistence ISqliteBackedPersistence.OverrideAutoCreateResources(AutoCreate autoCreate)
    {
        AutoCreate = autoCreate;
        return this;
    }

    ISqliteBackedPersistence ISqliteBackedPersistence.SchemaName(string schemaName)
    {
        EnvelopeStorageSchemaName = schemaName;
        return this;
    }

    ISqliteBackedPersistence ISqliteBackedPersistence.OverrideScheduledJobLockId(int lockId)
    {
        _scheduledJobLockId = lockId;
        return this;
    }

    ISqliteBackedPersistence ISqliteBackedPersistence.EnableCommandQueues(bool enabled)
    {
        CommandQueuesEnabled = enabled;
        return this;
    }
}
