using System.Data.Common;
using System.Linq.Expressions;
using FastExpressionCompiler;
using ImTools;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;
using Weasel.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public class TenantedDbContextBuilderByDbDataSource<T> : IDbContextBuilder<T> where T : DbContext
{
    private readonly Action<DbContextOptionsBuilder<T>, DbDataSource, TenantId> _configuration;
    private readonly IDomainEventScraper[] _domainScrapers;

    private readonly Func<DbContextOptions<T>, T> _constructor;

    private readonly IServiceProvider _serviceProvider;
    private readonly MultiTenantedMessageStore _store;
    private ImHashMap<string, DbDataSource> _dataSources = ImHashMap<string, DbDataSource>.Empty;

    // Going to assume that it's wolverine enabled here!
    public TenantedDbContextBuilderByDbDataSource(IServiceProvider serviceProvider, MultiTenantedMessageStore store,
        Action<DbContextOptionsBuilder<T>, DbDataSource, TenantId> configuration,
        IEnumerable<IDomainEventScraper> domainScrapers)
    {
        _serviceProvider = serviceProvider;
        _store = store;
        
        _configuration = configuration;
        _domainScrapers = domainScrapers.ToArray();
        var optionsType = typeof(DbContextOptions<T>);
        var ctor = typeof(T).GetConstructors().FirstOrDefault(x =>
            x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == optionsType);

        if (ctor == null)
        {
            throw new InvalidOperationException(
                $"DbContext type {typeof(T).FullNameInCode()} must have a public constructor that accepts {optionsType.FullNameInCode()} as its only argument");
        }

        var options = Expression.Parameter(optionsType);
        var callCtor = Expression.New(ctor, options);
        _constructor = Expression.Lambda<Func<DbContextOptions<T>, T>>(callCtor, options).CompileFast();
    }


    public async ValueTask<T> BuildAndEnrollAsync(MessageContext messaging, CancellationToken cancellationToken)
    {
        var dataSource = await findDataSource(messaging.TenantId);
        if (dataSource == null)
        {
            throw new InvalidOperationException(
                $"Unable to find a DbDataSource for tenant '{messaging.TenantId}'");
        }

        var builder = new DbContextOptionsBuilder<T>();

        builder.UseApplicationServiceProvider(_serviceProvider);
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        _configuration(builder, dataSource, new TenantId(messaging.TenantId));
        
        var dbContext = _constructor(builder.Options);

        var transaction = new EfCoreEnvelopeTransaction(dbContext, messaging, _domainScrapers);
        await messaging.EnlistInOutboxAsync(transaction);

        return dbContext;
    }

    public ValueTask<T> BuildAsync(CancellationToken cancellationToken)
    {
        return BuildAsync(StorageConstants.DefaultTenantId, cancellationToken);
    }

    public async Task ApplyAllChangesToDatabasesAsync()
    {
        // For data source connections, ensure all tenant databases exist FIRST before
        // building DbContexts. NpgsqlDataSource strips credentials from its ConnectionString
        // property, so Weasel's EnsureDatabaseExistsAsync cannot create admin connections.
        // We use the admin data source directly instead.
        await ensureAllTenantDatabasesExistAsync();

        var contexts = await BuildAllAsync();

        foreach (var context in contexts)
        {
            await using var migration = await _serviceProvider.CreateMigrationAsync(context, CancellationToken.None);
            await migration.ExecuteAsync(AutoCreate.CreateOrUpdate, CancellationToken.None);
        }
    }

    public async Task EnsureAllDatabasesAreCreatedAsync()
    {
        // For data source connections, ensure all tenant databases exist FIRST before
        // building DbContexts. NpgsqlDataSource strips credentials from its ConnectionString
        // property, so Weasel's EnsureDatabaseExistsAsync cannot create admin connections.
        await ensureAllTenantDatabasesExistAsync();
    }

    private async Task ensureAllTenantDatabasesExistAsync()
    {
        var adminDataSource = _store.Main.As<IMessageDatabase>().Settings.DataSource;
        if (adminDataSource == null) return;

        await _store.Source.RefreshLiteAsync();

        var databaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in _store.Source.AllActiveByTenant())
        {
            var settings = assignment.Value.As<IMessageDatabase>().Settings;
            var connStr = settings.ConnectionString ?? settings.DataSource?.ConnectionString;
            if (string.IsNullOrEmpty(connStr)) continue;

            var builder = new DbConnectionStringBuilder { ConnectionString = connStr };
            if (builder.TryGetValue("Database", out var dbObj) && dbObj is string dbName && !string.IsNullOrEmpty(dbName))
            {
                databaseNames.Add(dbName);
            }
        }

        if (databaseNames.Count == 0) return;

        await using var adminConn = adminDataSource.CreateConnection();
        await adminConn.OpenAsync();

        foreach (var dbName in databaseNames)
        {
            await using var checkCmd = adminConn.CreateCommand();
            checkCmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = @dbname";
            var param = checkCmd.CreateParameter();
            param.ParameterName = "dbname";
            param.Value = dbName;
            checkCmd.Parameters.Add(param);

            if (await checkCmd.ExecuteScalarAsync() != null) continue;

            await using var createCmd = adminConn.CreateCommand();
            createCmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await createCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<IReadOnlyList<DbContext>> FindAllAsync()
    {
        var all = await BuildAllAsync();
        return all;
    }

    public DatabaseCardinality Cardinality => _store.Source.Cardinality;

    public async ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken)
    {
        var connectionString = await findDataSource(tenantId);
        var builder = new DbContextOptionsBuilder<T>();
        builder.UseApplicationServiceProvider(_serviceProvider);
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        
        _configuration(builder, connectionString, new TenantId(tenantId));
        var dbContext = _constructor(builder.Options);

        return dbContext;
    }

    public DbContext BuildForMain()
    {
        return _constructor(BuildOptionsForMain());
    }


    public DbContextOptions<T> BuildOptionsForMain()
    {
        var dataSource = _store.Main.As<IMessageDatabase>().Settings.DataSource;
        var builder = new DbContextOptionsBuilder<T>();
        builder.UseApplicationServiceProvider(_serviceProvider);
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        _configuration(builder, dataSource, new TenantId(StorageConstants.DefaultTenantId));
        return builder.Options;
    }

    public Type DbContextType => typeof(T);

    public async Task<IReadOnlyList<T>> BuildAllAsync()
    {
        var list = new List<T>();
        list.Add((T)BuildForMain());

        await _store.Source.RefreshAsync();
        
        foreach (var assignment in _store.Source.AllActiveByTenant())
        {
            var dbContext = await BuildAsync(assignment.TenantId, CancellationToken.None);
            list.Add(dbContext);
        }

        // Filter out duplicates when multiple tenants address the same database
        return list.GroupBy(x => x.Database.GetConnectionString()).Select(x => x.First()).ToList();
    }

    public async Task DeleteAllTenantDatabasesAsync()
    {
        await _store.Source.RefreshAsync();
        foreach (var assignment in _store.Source.AllActiveByTenant())
        {
            var dbContext = await BuildAsync(assignment.TenantId, CancellationToken.None);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    public async Task EnsureAllTenantDatabasesCreatedAsync()
    {
        await _store.Source.RefreshAsync();
        foreach (var assignment in _store.Source.AllActiveByTenant())
        {
            var dbContext = await BuildAsync(assignment.TenantId, CancellationToken.None);
            await _serviceProvider.EnsureDatabaseExistsAsync(dbContext);
            await using var migration = await _serviceProvider.CreateMigrationAsync(dbContext, CancellationToken.None);
            await migration.ExecuteAsync(AutoCreate.CreateOrUpdate, CancellationToken.None);
        }
    }

    private async Task<DbDataSource> findDataSource(string? tenantId)
    {
        tenantId ??= StorageConstants.DefaultTenantId;
        if (_dataSources.TryFind(tenantId, out var dataSource))
        {
            return dataSource;
        }

        if (tenantId.IsDefaultTenant())
        {
            dataSource = _store.Main.As<IMessageDatabase>().Settings.DataSource;
        }
        else
        {
            var tenant = await _store.Source.FindAsync(tenantId);
            var databaseSettings = tenant.As<IMessageDatabase>().Settings;
            dataSource = databaseSettings.DataSource;
        }

        _dataSources = _dataSources.AddOrUpdate(tenantId, dataSource);

        return dataSource!;
    }
}