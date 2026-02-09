using System.Linq.Expressions;
using FastExpressionCompiler;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Weasel.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public class TenantedDbContextBuilderByConnectionString<T> : IDbContextBuilder<T>  where T : DbContext
{
    private readonly Action<DbContextOptionsBuilder<T>, ConnectionString, TenantId> _configuration;

    private readonly Func<DbContextOptions<T>, T> _constructor;

    private readonly IServiceProvider _serviceProvider;
    private readonly MultiTenantedMessageStore _store;
    private ImHashMap<string, string> _connectionStrings = ImHashMap<string, string>.Empty;
    private readonly IDomainEventScraper[] _domainScrapers;

    // Going to assume that it's wolverine enabled here!
    public TenantedDbContextBuilderByConnectionString(IServiceProvider serviceProvider, MultiTenantedMessageStore store,
        Action<DbContextOptionsBuilder<T>, ConnectionString, TenantId> configuration,
        IEnumerable<IDomainEventScraper> domainScrapers)
    {
        _serviceProvider = serviceProvider;
        _store = store;
        _domainScrapers = domainScrapers.ToArray();

        _configuration = configuration;
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
        var connectionString = await findConnectionString(messaging.TenantId);
        if (connectionString.IsEmpty())
            throw new InvalidOperationException(
                $"Unable to find a database connection string for tenant '{messaging.TenantId}'");

        var builder = new DbContextOptionsBuilder<T>();

        builder.UseApplicationServiceProvider(_serviceProvider);
        
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        _configuration(builder, new ConnectionString(connectionString), new TenantId(messaging.TenantId));
        var dbContext = _constructor(builder.Options);

        var transaction = new EfCoreEnvelopeTransaction(dbContext, messaging, _domainScrapers);

        await messaging.EnlistInOutboxAsync(transaction);

        return dbContext;
    }

    public ValueTask<T> BuildAsync(CancellationToken cancellationToken)
    {
        return BuildAsync(StorageConstants.DefaultTenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<T>> BuildAllAsync()
    {
        var list = new List<T>();
        list.Add((T)BuildForMain());
        
        await _store.Source.RefreshLiteAsync();
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
            await dbContext.Database.EnsureCreatedAsync();
        }
    }

    public async Task ApplyAllChangesToDatabasesAsync()
    {
        var contexts = await BuildAllAsync();

        foreach (var context in contexts)
        {
            await context.Database.EnsureCreatedAsync();
            await using var migration = await _serviceProvider.CreateMigrationAsync(context, CancellationToken.None);
            
            // TODO -- add some logging here!
            await migration.ExecuteAsync(AutoCreate.CreateOrUpdate, CancellationToken.None);
        }
    }

    public async Task EnsureAllDatabasesAreCreatedAsync()
    {
        var contexts = await BuildAllAsync();

        foreach (var context in contexts)
        {
            // TODO -- let's put some debug logging here!!!!
            await context.Database.EnsureCreatedAsync();
        }
    }


    public async ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken)
    {
        var connectionString = await findConnectionString(tenantId);
        var builder = new DbContextOptionsBuilder<T>();
        builder.UseApplicationServiceProvider(_serviceProvider);
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        
        _configuration(builder, new ConnectionString(connectionString), new TenantId(tenantId));
        var dbContext = _constructor(builder.Options);

        return dbContext;
    }

    private async Task<string> findConnectionString(string? tenantId)
    {
        tenantId ??= StorageConstants.DefaultTenantId;
        if (_connectionStrings.TryFind(tenantId, out var connectionString))
        {
            return connectionString;
        }

        if (tenantId.IsDefaultTenant())
        {
            connectionString = _store.Main.As<IMessageDatabase>().Settings.ConnectionString;
        }
        else
        {
            var tenant = await _store.Source.FindAsync(tenantId);
            var databaseSettings = tenant.As<IMessageDatabase>().Settings;
            connectionString = databaseSettings.ConnectionString ?? databaseSettings.DataSource?.ConnectionString;
        }

        _connectionStrings = _connectionStrings.AddOrUpdate(tenantId, connectionString);

        return connectionString!;
    }

    public DbContext BuildForMain()
    {
        return _constructor(BuildOptionsForMain());
    }
    
    
    public DbContextOptions<T> BuildOptionsForMain()
    {
        var connectionString = _store.Main.As<IMessageDatabase>().Settings.ConnectionString;
        var builder = new DbContextOptionsBuilder<T>();
        builder.UseApplicationServiceProvider(_serviceProvider);
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        _configuration(builder, new ConnectionString(connectionString), new TenantId(StorageConstants.DefaultTenantId));
        return builder.Options;
    }

    public Type DbContextType => typeof(T);

    public async Task<IReadOnlyList<DbContext>> FindAllAsync()
    {
        var all = await BuildAllAsync();
        return all;
    }

    public DatabaseCardinality Cardinality => _store.Source.Cardinality;
}