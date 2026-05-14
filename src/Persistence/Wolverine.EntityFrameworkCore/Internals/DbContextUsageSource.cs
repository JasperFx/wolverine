using System.Diagnostics.CodeAnalysis;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// Bridge that walks a single-database <see cref="DbContext"/> registration
/// and produces a <see cref="DbContextUsage"/> snapshot for monitoring tools
/// (CritterWatch #102). The EF Core parallel of Marten's
/// <c>DocumentStore.TryCreateUsage</c>: hand-built first-class properties for
/// the operationally-interesting bits, flat OptionValues for the secondary
/// settings, per-entity <see cref="EntityDescriptor"/> for each entity type.
/// </summary>
/// <remarks>
/// Resolves <typeparamref name="T"/> from a fresh request scope so the
/// snapshot reads the same configuration the application would observe at
/// runtime. Connection strings are intentionally NOT extracted from the
/// underlying <see cref="DbContext"/> connection — only server / database /
/// schema metadata via <see cref="DatabaseDescriptor"/>. Multi-tenant
/// contexts use the <see cref="TenantedDbContextUsageSource{T}"/> variant.
/// </remarks>
public sealed class DbContextUsageSource<T> : IDbContextUsageSource where T : DbContext
{
    private readonly IServiceProvider _services;

    public DbContextUsageSource(IServiceProvider services)
    {
        _services = services;
    }

    public Uri Subject { get; } = new($"efcore://{typeof(T).Name}");

    public Task<DbContextUsage?> TryCreateUsage(CancellationToken token)
    {
        try
        {
            using var scope = _services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<T>();

            var usage = DbContextUsageFactory.Build(
                Subject,
                dbContext,
                tenancyStyle: "Single",
                _services,
                tenantDatabases: null);
            return Task.FromResult<DbContextUsage?>(usage);
        }
        catch
        {
            // Snapshot is best-effort — a transient configuration / DI failure
            // shouldn't poison the entire ServiceCapabilities read.
            return Task.FromResult<DbContextUsage?>(null);
        }
    }
}

/// <summary>
/// Bridge for a Wolverine-managed multi-tenant <see cref="DbContext"/>:
/// resolves the per-tenant <see cref="IDbContextBuilder{T}"/>, reads the
/// model from <c>BuildForMain</c> for representative introspection, and
/// pulls the per-tenant <see cref="DatabaseDescriptor"/> list via
/// <see cref="IDbContextBuilder.FindAllAsync"/>.
/// </summary>
/// <remarks>
/// Connection strings are masked: the bridge reports server, database name,
/// and tenant id only — never the raw connection string. The model on
/// <c>BuildForMain</c> is identical to every per-tenant context's model
/// (multi-tenancy operates at the connection level, not the model level), so
/// per-entity descriptors and saga discovery only run once.
/// </remarks>
public sealed class TenantedDbContextUsageSource<T> : IDbContextUsageSource where T : DbContext
{
    private readonly IServiceProvider _services;

    public TenantedDbContextUsageSource(IServiceProvider services)
    {
        _services = services;
    }

    public Uri Subject { get; } = new($"efcore://{typeof(T).Name}");

    public async Task<DbContextUsage?> TryCreateUsage(CancellationToken token)
    {
        try
        {
            var builder = _services.GetRequiredService<IDbContextBuilder<T>>();

            // Tenancy-style discriminator from the registered IDbContextBuilder
            // implementation type — keeps the badge in operator vocabulary.
            var tenancyStyle = builder.GetType().Name switch
            {
                var n when n.StartsWith("TenantedDbContextBuilderByDbDataSource") => "DbDataSource",
                var n when n.StartsWith("TenantedDbContextBuilderByConnectionString") => "ConnectionString",
                _ => "Single"
            };

            // Read the model + change-tracker config from the main context
            // (representative of every tenant's context).
            var mainContext = (T)builder.BuildForMain();

            // Per-tenant database descriptors — masked to server / database /
            // schema only via DatabaseDescriptorFactory below.
            var tenantContexts = await builder.FindAllAsync();
            var tenantDatabases = tenantContexts
                .Select(DatabaseDescriptorFactory.FromDbContext)
                .ToList();

            try
            {
                return DbContextUsageFactory.Build(
                    Subject,
                    mainContext,
                    tenancyStyle,
                    _services,
                    tenantDatabases);
            }
            finally
            {
                // DbContext implements IAsyncDisposable since EF Core 3.0 — and
                // mainContext is statically typed T : DbContext — so always
                // prefer DisposeAsync() to satisfy VSTHRD103 and avoid
                // unnecessarily blocking on synchronous Dispose() in this
                // async snapshot path. The previous if/else split was dead
                // code: every concrete T reachable here is IAsyncDisposable.
                await mainContext.DisposeAsync();
            }
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Shared snapshot-build logic — extracted so both the single-DB and
/// tenanted source paths produce identical descriptor shapes for everything
/// that doesn't depend on the per-tenant fan-out.
/// </summary>
internal static class DbContextUsageFactory
{
    public static DbContextUsage Build(
        Uri subjectUri,
        DbContext dbContext,
        string tenancyStyle,
        IServiceProvider services,
        IReadOnlyList<DatabaseDescriptor>? tenantDatabases)
    {
        var model = dbContext.Model;

        var usage = new DbContextUsage(subjectUri, dbContext)
        {
            ProviderName = dbContext.Database.ProviderName ?? "",
            TenancyStyle = tenancyStyle,
            WolverineEnabled = dbContext.IsWolverineEnabled(),
        };

        // Database topology — server / database / schema / cardinality. For
        // multi-tenant contexts we already built the per-tenant list above;
        // for single-DB contexts we just describe the connection.
        if (tenantDatabases is { Count: > 0 })
        {
            usage.Database = new DatabaseUsage
            {
                Cardinality = tenancyStyle == "Single"
                    ? DatabaseCardinality.Single
                    : DatabaseCardinality.DynamicMultiple,
                MainDatabase = tenantDatabases[0],
                Databases = tenantDatabases.ToList()
            };
        }
        else
        {
            var mainDatabase = DatabaseDescriptorFactory.FromDbContext(dbContext);
            usage.Database = new DatabaseUsage
            {
                Cardinality = DatabaseCardinality.Single,
                MainDatabase = mainDatabase,
                Databases = [mainDatabase]
            };
        }

        // Probe Wolverine integration shape from runtime services. These
        // probes don't throw if Wolverine isn't fully wired — they return
        // sane defaults so untracked DbContexts get a meaningful snapshot.
        usage.TransactionMode = ProbeTransactionMode(services);
        usage.DomainEventsMode = ProbeDomainEventsMode(services, usage);
        usage.WolverineMigrations = ProbeWolverineMigrations(services);
        usage.OutboxIntegration = ProbeOutboxIntegration(services, usage.WolverineEnabled);

        // Per-entity descriptors and saga roll-up. EF Core entity types are
        // walked once; saga types lift into both the per-entity IsSaga flag
        // AND the top-level SagaTypes list for direct cross-link from the UI.
        foreach (var entityType in model.GetEntityTypes().OrderBy(e => e.ClrType.FullNameInCode()))
        {
            var entityDescriptor = BuildEntityDescriptor(entityType);
            usage.Entities.Add(entityDescriptor);

            if (entityDescriptor.IsSaga)
            {
                usage.SagaTypes.Add(entityDescriptor.EntityType);
            }
        }

        // Flat OptionValues — change-tracker behaviour, sensitive-data flag.
        var changeTracker = dbContext.ChangeTracker;
        usage.AddValue(nameof(changeTracker.AutoDetectChangesEnabled), changeTracker.AutoDetectChangesEnabled);
        usage.AddValue(nameof(changeTracker.QueryTrackingBehavior), changeTracker.QueryTrackingBehavior.ToString());
        usage.AddValue(nameof(changeTracker.LazyLoadingEnabled), changeTracker.LazyLoadingEnabled);

        // Surface per-entity domain-event mappings for the PerEntityType mode
        // — operators expanding the badge see exactly which entity publishes
        // which event type, without having to crack open code.
        ApplyPerEntityScraperOptionValues(services, usage);

        return usage;
    }

    private static EntityDescriptor BuildEntityDescriptor(IEntityType entityType)
    {
        var descriptor = new EntityDescriptor
        {
            EntityType = TypeDescriptor.For(entityType.ClrType),
            Schema = entityType.GetSchema() ?? "",
            TableName = entityType.GetTableName() ?? entityType.ClrType.Name,
            IsView = !string.IsNullOrEmpty(entityType.GetViewName()),
            ViewName = entityType.GetViewName(),
            IsOwned = entityType.IsOwned(),
            IsSaga = entityType.ClrType.CanBeCastTo<Saga>(),
            HasQueryFilter = HasAnyQueryFilter(entityType),
        };

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey != null)
        {
            descriptor.PrimaryKey = string.Join(", ", primaryKey.Properties.Select(p => p.GetColumnName() ?? p.Name));
        }

        var concurrencyToken = entityType.GetProperties()
            .FirstOrDefault(p => p.IsConcurrencyToken);
        if (concurrencyToken != null)
        {
            descriptor.ConcurrencyToken = concurrencyToken.GetColumnName() ?? concurrencyToken.Name;
        }

        var discriminator = entityType.FindDiscriminatorProperty();
        if (discriminator != null)
        {
            descriptor.Discriminator = discriminator.GetColumnName() ?? discriminator.Name;
        }

        foreach (var index in entityType.GetIndexes())
        {
            descriptor.Indexes.Add(new IndexDescriptor
            {
                Name = index.GetDatabaseName() ?? "",
                IsUnique = index.IsUnique,
                Columns = index.Properties
                    .Select(p => p.GetColumnName() ?? p.Name)
                    .ToList()
            });
        }

        foreach (var fk in entityType.GetForeignKeys())
        {
            // Skip self-referencing FKs — operator noise.
            if (fk.PrincipalEntityType == entityType) continue;

            var targetTable = fk.PrincipalEntityType.GetTableName() ?? "";
            var targetSchema = fk.PrincipalEntityType.GetSchema();
            if (!string.IsNullOrEmpty(targetSchema))
            {
                targetTable = $"{targetSchema}.{targetTable}";
            }

            descriptor.ForeignKeys.Add(new ForeignKeyDescriptor
            {
                Name = fk.GetConstraintName() ?? "",
                TargetTable = targetTable,
                KeyColumns = fk.Properties
                    .Select(p => p.GetColumnName() ?? p.Name)
                    .ToList()
            });
        }

        return descriptor;
    }

    /// <summary>
    /// Detects whether <paramref name="entityType"/> has any global query
    /// filter configured. EF 8/9 expose <c>GetQueryFilter()</c> returning a
    /// single <c>LambdaExpression?</c>; EF 10 added
    /// <c>GetDeclaredQueryFilters()</c> returning a collection. Reflection
    /// here keeps this single source file compatible with all three EF Core
    /// majors that the wolverine repo targets.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Reflection over IEntityType implementation type's GetDeclaredQueryFilters / GetQueryFilter methods to maintain compatibility with EF 8/9 (single LambdaExpression) and EF 10 (collection). The IEntityType is provided by EF Core at runtime and its concrete type's methods are preserved by EF Core itself.")]
    private static bool HasAnyQueryFilter(IEntityType entityType)
    {
        var type = entityType.GetType();
        var declared = type.GetMethod("GetDeclaredQueryFilters", Type.EmptyTypes);
        if (declared != null)
        {
            var result = declared.Invoke(entityType, null);
            if (result is System.Collections.IEnumerable enumerable)
            {
                foreach (var _ in enumerable)
                {
                    return true;
                }
            }
            return false;
        }

        var legacy = type.GetMethod("GetQueryFilter", Type.EmptyTypes);
        if (legacy != null)
        {
            return legacy.Invoke(entityType, null) != null;
        }

        return false;
    }

    private static string? ProbeTransactionMode(IServiceProvider services)
    {
        // Pull from WolverineRuntime if present; fall back to null when
        // Wolverine isn't driving the transaction lifetime.
        var runtime = services.GetService<IWolverineRuntime>();
        if (runtime == null) return null;

        var providers = runtime.Options.CodeGeneration.PersistenceProviders();
        var efProvider = providers.OfType<EFCorePersistenceFrameProvider>().FirstOrDefault();
        return efProvider?.DefaultMode.ToString();
    }

    private static string ProbeDomainEventsMode(IServiceProvider services, DbContextUsage usage)
    {
        var scrapers = services.GetServices<IDomainEventScraper>().ToArray();
        if (scrapers.Length == 0) return "None";

        var hasOutgoing = scrapers.Any(s => s is OutgoingDomainEventsScraper);
        var perEntity = scrapers
            .Where(s => s.GetType().IsGenericType
                        && s.GetType().GetGenericTypeDefinition() == typeof(DomainEventScraper<,>))
            .ToArray();

        if (perEntity.Length > 0) return "PerEntityType";
        if (hasOutgoing) return "OutgoingDomainEvents";
        return "None";
    }

    private static void ApplyPerEntityScraperOptionValues(IServiceProvider services, DbContextUsage usage)
    {
        if (usage.DomainEventsMode != "PerEntityType") return;

        var scrapers = services.GetServices<IDomainEventScraper>()
            .Where(s => s.GetType().IsGenericType
                        && s.GetType().GetGenericTypeDefinition() == typeof(DomainEventScraper<,>))
            .ToArray();

        for (var i = 0; i < scrapers.Length; i++)
        {
            var scraperType = scrapers[i].GetType();
            var args = scraperType.GetGenericArguments();
            if (args.Length != 2) continue;

            usage.AddValue($"DomainEventScraper[{i}].EntityType", args[0].FullNameInCode());
            usage.AddValue($"DomainEventScraper[{i}].EventType", args[1].FullNameInCode());
        }
    }

    private static bool ProbeWolverineMigrations(IServiceProvider services)
    {
        // EntityFrameworkCoreSystemPart only registers when
        // UseEntityFrameworkCoreWolverineManagedMigrations was called.
        // GetServices avoids resolution side-effects and lets us test
        // for presence without touching the singleton.
        return services
            .GetServices<ISystemPart>()
            .Any(p => p.GetType().FullName == "Wolverine.EntityFrameworkCore.Internals.Migrations.EntityFrameworkCoreSystemPart");
    }

    private static string ProbeOutboxIntegration(IServiceProvider services, bool wolverineEnabled)
    {
        if (wolverineEnabled) return "Mapped";
        var outboxFactory = services.GetService<IDbContextOutboxFactory>();
        return outboxFactory != null ? "ExternalConnection" : "None";
    }
}

/// <summary>
/// Builds <see cref="DatabaseDescriptor"/> instances from a live
/// <see cref="DbContext"/>'s connection metadata. Connection strings are
/// never read directly — only the server, database name, and schema are
/// surfaced so the snapshot is safe to ship to monitoring tools.
/// </summary>
internal static class DatabaseDescriptorFactory
{
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DatabaseDescriptor(subject) reads subject's runtime-type properties for diagnostic reporting. User DbContext subclass properties trimmed away are silently omitted, which is acceptable for this diagnostic surface.")]
    public static DatabaseDescriptor FromDbContext(DbContext dbContext)
    {
        var providerName = dbContext.Database.ProviderName ?? "";
        var engine = ProviderToEngine(providerName);

        var serverName = "";
        var databaseName = "";

        // EF Core's IRelationalConnection exposes DbConnection on relational
        // providers. The connection string is parsed by the underlying
        // ADO.NET provider for server/database extraction; we never store
        // the raw connection string. Non-relational providers (InMemory)
        // don't expose IRelationalConnection — for those we just leave
        // server / database blank.
        try
        {
            var relational = dbContext.GetService<IRelationalConnection>();
            if (relational?.DbConnection != null)
            {
                var conn = relational.DbConnection;
                serverName = conn.DataSource ?? "";
                databaseName = conn.Database ?? "";
            }
        }
        catch
        {
            // InMemory / non-relational providers throw when GetService<IRelationalConnection>
            // is called. Fall through with empty server/database.
        }

        return new DatabaseDescriptor(dbContext)
        {
            Engine = engine,
            ServerName = serverName,
            DatabaseName = databaseName,
            Identifier = dbContext.GetType().Name
        };
    }

    private static string ProviderToEngine(string providerName) => providerName switch
    {
        "Npgsql.EntityFrameworkCore.PostgreSQL" => "PostgreSQL",
        "Microsoft.EntityFrameworkCore.SqlServer" => "SqlServer",
        "Microsoft.EntityFrameworkCore.Sqlite" => "Sqlite",
        "Microsoft.EntityFrameworkCore.InMemory" => "InMemory",
        "Pomelo.EntityFrameworkCore.MySql" => "MySql",
        _ => providerName
    };
}
