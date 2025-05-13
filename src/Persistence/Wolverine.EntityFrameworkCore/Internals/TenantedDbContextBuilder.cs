using System.Linq.Expressions;
using FastExpressionCompiler;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public class TenantedDbContextBuilder<T> : IDbContextBuilder<T> where T : DbContext
{
    private readonly Action<DbContextOptionsBuilder<T>, string> _configuration;

    private readonly Func<DbContextOptions<T>, T> _constructor;

    private readonly IServiceProvider _serviceProvider;
    private readonly MultiTenantedMessageStore _store;
    private ImHashMap<string, string> _connectionStrings = ImHashMap<string, string>.Empty;

    // Going to assume that it's wolverine enabled here!
    public TenantedDbContextBuilder(IServiceProvider serviceProvider, MultiTenantedMessageStore store,
        Action<DbContextOptionsBuilder<T>, string> configuration)
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
        var connectionString = await findConnectionString(messaging.TenantId);
        if (connectionString.IsEmpty())
            throw new InvalidOperationException(
                $"Unable to find a database connection string for tenant '{messaging.TenantId}'");

        var builder = new DbContextOptionsBuilder<T>();

        builder.UseApplicationServiceProvider(_serviceProvider);
        
        builder.ReplaceService<IModelCustomizer, WolverineModelCustomizer>();
        _configuration(builder, connectionString);
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

    public async Task MigrateAllAsync()
    {
        await BuildForMain().Database.MigrateAsync();
        await _store.Source.RefreshAsync();
        foreach (var assignment in _store.Source.AllActiveByTenant())
        {
            var dbContext = await BuildAsync(assignment.TenantId, CancellationToken.None);
            await dbContext.Database.MigrateAsync();
        }
    }

    public async ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken)
    {
        var connectionString = await findConnectionString(tenantId);
        var builder = new DbContextOptionsBuilder<T>();
        _configuration(builder, connectionString);
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

        if (tenantId == string.Empty || tenantId == StorageConstants.DefaultTenantId)
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
        var connectionString = _store.Main.As<IMessageDatabase>().Settings.ConnectionString;
        var builder = new DbContextOptionsBuilder<T>();
        _configuration(builder, connectionString);
        var dbContext = _constructor(builder.Options);

        return dbContext;
    }

    public Type DbContextType => typeof(T);
}