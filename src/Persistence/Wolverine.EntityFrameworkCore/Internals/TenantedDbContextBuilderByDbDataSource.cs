using System.Data.Common;
using System.Linq.Expressions;
using FastExpressionCompiler;
using ImTools;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public class TenantedDbContextBuilderByDbDataSource<T> : IDbContextBuilder<T> where T : DbContext
{
    private readonly Action<DbContextOptionsBuilder<T>, DbDataSource, TenantId> _configuration;

    private readonly Func<DbContextOptions<T>, T> _constructor;

    private readonly IServiceProvider _serviceProvider;
    private readonly MultiTenantedMessageStore _store;
    private ImHashMap<string, DbDataSource> _dataSources = ImHashMap<string, DbDataSource>.Empty;

    // Going to assume that it's wolverine enabled here!
    public TenantedDbContextBuilderByDbDataSource(IServiceProvider serviceProvider, MultiTenantedMessageStore store,
        Action<DbContextOptionsBuilder<T>, DbDataSource, TenantId> configuration)
    {
        _serviceProvider = serviceProvider;
        _store = store;
        
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

        var transaction = new MappedEnvelopeTransaction(dbContext, messaging);
        // ReSharper disable once MethodHasAsyncOverload
        messaging.EnlistInOutbox(transaction);

        return dbContext;
    }

    public ValueTask<T> BuildAsync(CancellationToken cancellationToken)
    {
        return BuildAsync(StorageConstants.DefaultTenantId, cancellationToken);
    }

    public async Task ApplyAllChangesToDatabasesAsync()
    {
        var contexts = await BuildAllAsync();

        foreach (var context in contexts)
        {
            var pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();
            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToArray();

            if (pending.All(x => applied.Contains(x))) return;

            var migrator = context.Database.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync();
        }
    }

    public async Task EnsureAllDatabasesAreCreatedAsync()
    {
        var contexts = await BuildAllAsync();

        foreach (var context in contexts)
        {
            // TODO -- let's put some debug logging here!!!!
            await context.Database.MigrateAsync();
        }
    }

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
            await dbContext.Database.EnsureCreatedAsync();
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