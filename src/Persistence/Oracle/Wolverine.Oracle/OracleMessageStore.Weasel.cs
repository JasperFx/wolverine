using JasperFx;
using JasperFx.Descriptors;
using Oracle.ManagedDataAccess.Client;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Oracle;
using Wolverine.RDBMS;

namespace Wolverine.Oracle;

// GH-5172. The other relational stores derive from MessageDatabase<T>, which is a Weasel DatabaseBase<T>,
// so MessageDatabaseDiscovery finds them as Weasel IDatabase instances and the db-list / db-dump /
// db-patch / db-assert / db-apply commands work against them. OracleMessageStore implements
// IMessageDatabase directly, so it was never an IDatabase and those commands saw zero databases.
// It now is one, delegating to a small DatabaseBase<OracleConnection> over its own AllObjects().
internal partial class OracleMessageStore : IDatabase
{
    private OracleWeaselDatabase? _weaselDatabase;

    private OracleWeaselDatabase weaselDatabase => _weaselDatabase ??= new OracleWeaselDatabase(this);

    public AutoCreate AutoCreate => _settings.AutoCreate;

    public Migrator Migrator => weaselDatabase.Migrator;

    [Obsolete("Try to favor the Id value object going forward")]
    public string Identifier => weaselDatabase.Identifier;

    public DatabaseId Id => weaselDatabase.Id;

    public IFeatureSchema[] BuildFeatureSchemas() => weaselDatabase.BuildFeatureSchemas();

    public string[] AllSchemaNames() => weaselDatabase.AllSchemaNames();

    public Task<SchemaMigration> CreateMigrationAsync(IFeatureSchema group, CancellationToken ct = default)
        => weaselDatabase.CreateMigrationAsync(group, ct);

    public string ToDatabaseScript() => weaselDatabase.ToDatabaseScript();

    public Task WriteCreationScriptToFileAsync(string filename, CancellationToken ct = default)
        => weaselDatabase.WriteCreationScriptToFileAsync(filename, ct);

    public Task WriteScriptsByTypeAsync(string directory, CancellationToken ct = default)
        => weaselDatabase.WriteScriptsByTypeAsync(directory, ct);

    public Task<SchemaMigration> CreateMigrationAsync(CancellationToken ct = default)
        => weaselDatabase.CreateMigrationAsync(ct);

    public Task<SchemaPatchDifference> ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate? @override = null,
        ReconnectionOptions? reconnectionOptions = null, CancellationToken ct = default)
        => weaselDatabase.ApplyAllConfiguredChangesToDatabaseAsync(@override, reconnectionOptions, ct);

    public Task AssertDatabaseMatchesConfigurationAsync(CancellationToken ct = default)
        => weaselDatabase.AssertDatabaseMatchesConfigurationAsync(ct);

    public Task AssertConnectivityAsync(CancellationToken ct = default)
        => weaselDatabase.AssertConnectivityAsync(ct);

    public ValueTask ReleaseConnectionPoolAsync(CancellationToken ct = default)
        => weaselDatabase.ReleaseConnectionPoolAsync(ct);

    /// <summary>
    /// The Weasel view of an OracleMessageStore: one feature holding every table the store provisions,
    /// named the same as the MessageDatabase-based stores ("WolverineEnvelopeStorage").
    /// </summary>
    private class OracleWeaselDatabase : DatabaseBase<OracleConnection>, IFeatureSchema
    {
        private readonly OracleMessageStore _store;

        public OracleWeaselDatabase(OracleMessageStore store) : base(new MigrationLogger(store._logger),
            store._settings.AutoCreate, new OracleMigrator(), "WolverineEnvelopeStorage",
            () => store._dataSource.CreateConnection())
        {
            _store = store;

            var descriptor = store.Describe();
            Id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
        }

        public override DatabaseDescriptor Describe() => _store.Describe();

        public override IFeatureSchema[] BuildFeatureSchemas() => [this];

        public ISchemaObject[] Objects => _store.AllObjects().ToArray();

        Type IFeatureSchema.StorageType => typeof(OracleMessageStore);

        void IFeatureSchema.WritePermissions(Migrator rules, TextWriter writer)
        {
            // Nothing
        }

        IEnumerable<Type> IFeatureSchema.DependentTypes()
        {
            yield break;
        }
    }
}
