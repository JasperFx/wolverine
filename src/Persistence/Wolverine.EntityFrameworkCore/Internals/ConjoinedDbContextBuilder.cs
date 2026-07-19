using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FastExpressionCompiler;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;
using Weasel.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals.Migrations;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

// AOT note (#2746): same ctor lookup + expression-compiled factory pattern as the
// tenanted builders; AOT consumers register the DbContext factory explicitly
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "FastExpressionCompiler.CompileFast at registration; AOT consumers register an explicit factory. See AOT guide / #2755.")]
[UnconditionalSuppressMessage("Trimming", "IL2090",
    Justification = "DbContext T parameter accessed for ctor lookup; T is statically rooted by AddWolverineEFCore<T>(). See AOT guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "FastExpressionCompiler emits IL at registration time; AOT consumers register an explicit factory. See AOT guide / #2755.")]
public class ConjoinedDbContextBuilder<T> : IDbContextBuilder<T> where T : DbContext
{
    private readonly Action<DbContextOptionsBuilder<T>, ConnectionString> _configuration;
    private readonly Func<DbContextOptions<T>, T> _constructor;
    private readonly IMessageDatabase _database;
    private readonly IDomainEventScraper[] _domainScrapers;
    private readonly Lazy<DbContextOptions<T>> _options;
    private readonly IServiceProvider _serviceProvider;

    public ConjoinedDbContextBuilder(IServiceProvider serviceProvider, IMessageDatabase database,
        Action<DbContextOptionsBuilder<T>, ConnectionString> configuration,
        IEnumerable<IDomainEventScraper> domainScrapers)
    {
        _serviceProvider = serviceProvider;
        _database = database;
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

        // Conjoined tenancy is one shared database, so every context is built from
        // identical options
        _options = new Lazy<DbContextOptions<T>>(buildOptions);
    }

    public async ValueTask<T> BuildAndEnrollAsync(MessageContext messaging, CancellationToken cancellationToken)
    {
        var dbContext = build(messaging.TenantId);

        var transaction = new EfCoreEnvelopeTransaction(dbContext, messaging, _domainScrapers);
        await messaging.EnlistInOutboxAsync(transaction);

        return dbContext;
    }

    public ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken)
    {
        return new ValueTask<T>(build(tenantId));
    }

    public ValueTask<T> BuildAsync(CancellationToken cancellationToken)
    {
        return BuildAsync(StorageConstants.DefaultTenantId, cancellationToken);
    }

    public DbContext BuildForMain()
    {
        return build(StorageConstants.DefaultTenantId);
    }

    public DbContextOptions<T> BuildOptionsForMain()
    {
        return _options.Value;
    }

    public Type DbContextType => typeof(T);

    public async Task ApplyAllChangesToDatabasesAsync()
    {
        var context = build(StorageConstants.DefaultTenantId);
        await _serviceProvider.EnsureDatabaseExistsAsync(context);

        var options = ConjoinedTenancy.OptionsFor(typeof(T));
        if (options.PartitioningEnabled)
        {
            // Partitioned conjoined tables migrate through a Weasel database object so
            // the partition manager's initializer hydrates the tenant map before deltas
            // are computed, and the partition control table migrates with the entity tables
            var partitions = (ConjoinedTenantPartitions<T>)_serviceProvider
                .GetRequiredService<IConjoinedTenantPartitions<T>>();
            var database = await partitions.BuildWeaselDatabaseAsync(CancellationToken.None);
            await database.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate);
            return;
        }

        await using var migration = await _serviceProvider.CreateMigrationAsync(context, CancellationToken.None);
        await migration.ExecuteAsync(AutoCreate.CreateOrUpdate, CancellationToken.None);
    }

    public async Task EnsureAllDatabasesAreCreatedAsync()
    {
        var context = build(StorageConstants.DefaultTenantId);
        await _serviceProvider.EnsureDatabaseExistsAsync(context);
    }

    public Task<IReadOnlyList<DbContext>> FindAllAsync()
    {
        return Task.FromResult<IReadOnlyList<DbContext>>([BuildForMain()]);
    }

    public DatabaseCardinality Cardinality => DatabaseCardinality.Single;

    private T build(string? tenantId)
    {
        var dbContext = _constructor(_options.Value);
        ConjoinedTenancy.Pin(dbContext, tenantId);
        return dbContext;
    }

    private DbContextOptions<T> buildOptions()
    {
        var connectionString = _database.Settings.ConnectionString ?? _database.Settings.DataSource?.ConnectionString;
        if (connectionString.IsEmpty())
        {
            throw new InvalidOperationException(
                $"Unable to determine the database connection string for the conjoined multi-tenanted DbContext {typeof(T).FullNameInCode()}");
        }

        var builder = new DbContextOptionsBuilder<T>();
        builder.UseApplicationServiceProvider(_serviceProvider);
        builder.ReplaceService<IModelCustomizer, ConjoinedTenancyModelCustomizer>();
        builder.AddInterceptors(TenantStampingInterceptor.Instance);
        _configuration(builder, new ConnectionString(connectionString!));

        return builder.Options;
    }
}
