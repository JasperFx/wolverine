using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JasperFx.Core.Reflection;
using JasperFx.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// Read-only <see cref="IDocumentStoreDiagnostics"/> over one registered <see cref="DbContext"/>, so a
/// monitoring tool can list its entity types and page, filter and sort their rows the same way it does for
/// Marten, Polecat and Fisher documents (GH-5297). Registered next to <see cref="DbContextUsageSource{T}"/>
/// and reporting the same <c>efcore://{ContextName}</c> subject.
/// </summary>
/// <remarks>
/// <para>Every read opens a fresh scope and a fresh, no-tracking context. Rows are serialized from their
/// <b>scalar properties only</b>: navigations can cycle and can trigger lazy loading, and nothing here
/// ever calls <c>Include</c>. Owned types are not listed (they cannot be queried alone). Keyless and view
/// types are listed and can be paged, but have no id, so a load by id answers null and
/// <see cref="DocumentQueryOptions.IdEquals"/> is refused.</para>
/// <para>Tenancy goes through the context's own <see cref="IDbContextBuilder{T}"/>: a conjoined context is
/// pinned to the requested tenant, so its query filter does the scoping, and a database-per-tenant context
/// is built against that tenant's database. <see cref="DocumentQueryOptions.AllTenants"/> is refused on
/// either, because the only way to honour it on a conjoined context is to bypass the query filter, which
/// would also bypass whatever else the filter does.</para>
/// <para>This surface never saves: <c>SaveChanges</c> can run application interceptors and domain events,
/// which is a separate decision.</para>
/// </remarks>
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "Reflection-based STJ over entity scalar properties and Dynamic LINQ criteria; AOT consumers get a refusal from DocumentQueryCriteria. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2060",
    Justification = "Generic helpers are closed over entity types from the DbContext's own model, which EF Core roots. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2070",
    Justification = "Entity member walk over types from the DbContext's own model. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2075",
    Justification = "Entity member walk over types from the DbContext's own model. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2091",
    Justification = "Entity types come from the DbContext's own model, which EF Core roots. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2111",
    Justification = "Reflective invoke of private generic helpers on this type. See AOT guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "MakeGenericMethod over entity types from the DbContext's own model. See AOT guide.")]
public sealed class DbContextDocumentStoreDiagnostics<T> : IDocumentStoreDiagnostics where T : DbContext
{
    private static readonly MethodInfo _queryMethod = typeof(DbContextDocumentStoreDiagnostics<T>)
        .GetMethod(nameof(queryAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo _loadMethod = typeof(DbContextDocumentStoreDiagnostics<T>)
        .GetMethod(nameof(loadAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo _explainMethod = typeof(DbContextDocumentStoreDiagnostics<T>)
        .GetMethod(nameof(explainAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly IServiceProvider _services;

    public DbContextDocumentStoreDiagnostics(IServiceProvider services)
    {
        _services = services;
    }

    public Uri Subject { get; } = new($"efcore://{typeof(T).Name}");

    public async Task<IReadOnlyList<DocumentTypeRef>> DocumentTypesAsync(CancellationToken token)
    {
        await using var scope = _services.CreateAsyncScope();
        await using var dbContext = await buildAsync(scope.ServiceProvider, null, token).ConfigureAwait(false);

        var types = queryableEntityTypes(dbContext);
        return types
            .Select(e => new DocumentTypeRef(e.ClrType.FullNameInCode(), e.GetTableName() ?? e.ClrType.Name,
                e.GetSchema() ?? "")
            {
                RootTypeName = e.BaseType is { } root && types.Contains(root)
                    ? e.GetRootType().ClrType.FullNameInCode()
                    : null,
                IsSoftDeleted = false
            })
            .OrderBy(x => x.TypeName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<DocumentQueryResult> QueryDocumentsAsync(string documentTypeName, DocumentQueryOptions options,
        CancellationToken token)
    {
        var tenantId = assertTenancy(options);
        assertSupported(documentTypeName, options);

        await using var scope = _services.CreateAsyncScope();
        await using var dbContext = await buildAsync(scope.ServiceProvider, tenantId, token).ConfigureAwait(false);

        var entityType = findEntityType(dbContext, documentTypeName);
        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Clamp(options.PageSize, 1, 1000);
        if (entityType is null)
        {
            return new DocumentQueryResult(Array.Empty<StoredDocument>(), 0, pageNumber, pageSize);
        }

        var task = (Task<DocumentQueryResult>)_queryMethod.MakeGenericMethod(entityType.ClrType)
            .Invoke(null, [dbContext, entityType, options, tenantId, pageNumber, pageSize, token])!;
        return await task.ConfigureAwait(false);
    }

    public async Task<StoredDocument?> LoadDocumentAsync(string documentTypeName, string id, string? tenantId,
        CancellationToken token)
    {
        var tenant = DocumentQueryOptions.NormalizeTenantId(tenantId);

        await using var scope = _services.CreateAsyncScope();
        await using var dbContext = await buildAsync(scope.ServiceProvider, tenant, token).ConfigureAwait(false);

        var entityType = findEntityType(dbContext, documentTypeName);
        if (entityType is null || singleKey(entityType) is null)
        {
            return null;
        }

        var task = (Task<StoredDocument?>)_loadMethod.MakeGenericMethod(entityType.ClrType)
            .Invoke(null, [dbContext, entityType, id, tenant, token])!;
        return await task.ConfigureAwait(false);
    }

    public async Task<DocumentQueryPlan?> ExplainDocumentQueryAsync(string documentTypeName,
        DocumentQueryOptions options, CancellationToken token)
    {
        var tenantId = assertTenancy(options);
        assertSupported(documentTypeName, options);

        await using var scope = _services.CreateAsyncScope();
        await using var dbContext = await buildAsync(scope.ServiceProvider, tenantId, token).ConfigureAwait(false);

        var entityType = findEntityType(dbContext, documentTypeName);
        if (entityType is null)
        {
            return null;
        }

        var pageNumber = Math.Max(1, options.PageNumber);
        var pageSize = Math.Clamp(options.PageSize, 1, 1000);
        var task = (Task<DocumentQueryPlan?>)_explainMethod.MakeGenericMethod(entityType.ClrType)
            .Invoke(null, [dbContext, entityType, options, pageNumber, pageSize])!;
        return await task.ConfigureAwait(false);
    }

    // The tenant to read, or null for the default tenant. A conjoined or database-per-tenant context can't
    // honour AllTenants without bypassing its query filter or fanning out, so it says so rather than
    // answering with the default tenant's rows as though they were everyone's.
    private string? assertTenancy(DocumentQueryOptions options)
    {
        options.AssertValidTenantScope();

        var tenanted = _services.GetService<IDbContextBuilder<T>>() != null;
        if (options.AllTenants && tenanted)
        {
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.AllTenants),
                "this DbContext is multi-tenanted, and reading every tenant would bypass its tenant query filter; query one tenant at a time with TenantId");
        }

        return DocumentQueryOptions.NormalizeTenantId(options.TenantId);
    }

    private static void assertSupported(string documentTypeName, DocumentQueryOptions options)
    {
        // An EF Core entity captures none of these as metadata columns, so honouring them is impossible
        options.AssertMetadataFiltersAreCaptured(documentTypeName, false, false, false);

        if (options.OrderByMetadata != null)
        {
            throw new DocumentCriteriaNotSupportedException(nameof(DocumentQueryOptions.OrderByMetadata),
                "EF Core entities carry no last-modified, version or created metadata columns; order by one of the entity's own properties with OrderBy");
        }
    }

    // A fresh context per read. A multi-tenanted context comes from its builder so the tenant is pinned (or
    // the tenant's database chosen) the same way Wolverine does it for a message; anything else comes from DI
    private static async ValueTask<DbContext> buildAsync(IServiceProvider scoped, string? tenantId,
        CancellationToken token)
    {
        var builder = scoped.GetService<IDbContextBuilder<T>>();
        if (builder == null)
        {
            return scoped.GetRequiredService<T>();
        }

        return tenantId == null
            ? await builder.BuildAsync(token).ConfigureAwait(false)
            : await builder.BuildAsync(tenantId, token).ConfigureAwait(false);
    }

    // Everything a Set<T>() can be read from: not owned, not a shared-type (property bag) entity
    private static IReadOnlyCollection<IEntityType> queryableEntityTypes(DbContext dbContext)
    {
        return dbContext.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && !e.HasSharedClrType)
            .ToArray();
    }

    private static IEntityType? findEntityType(DbContext dbContext, string name)
    {
        var types = queryableEntityTypes(dbContext);
        return types.FirstOrDefault(e => e.ClrType.FullNameInCode() == name)
               ?? types.FirstOrDefault(e => e.ClrType.FullName == name)
               ?? types.SingleOrDefaultIfMany(e => e.ClrType.Name == name)
               ?? types.SingleOrDefaultIfMany(e => e.GetTableName() == name);
    }

    private static IProperty? singleKey(IEntityType entityType)
    {
        var key = entityType.FindPrimaryKey();
        return key is { Properties.Count: 1 } ? key.Properties[0] : null;
    }

    private static IQueryable<TEntity> buildQuery<TEntity>(DbContext dbContext, IEntityType entityType,
        DocumentQueryOptions options) where TEntity : class
    {
        IQueryable<TEntity> query = dbContext.Set<TEntity>().AsNoTracking();

        if (options.IdEquals != null)
        {
            var key = singleKey(entityType) ?? throw new DocumentCriteriaNotSupportedException(
                nameof(DocumentQueryOptions.IdEquals),
                $"{entityType.ClrType.FullNameInCode()} has no single-column primary key to compare an id with");

            if (!tryConvertId(options.IdEquals, key.ClrType, out var typedId))
            {
                // An id that can't be this type's identity matches nothing
                return query.Where(_ => false);
            }

            query = query.Where(keyEquals<TEntity>(key, typedId!));
        }

        // Order by the key when nothing else is asked for, and tie-break a given ordering by it, so paging
        // is deterministic. A key with no CLR member (shadow) can't be named to Dynamic LINQ, so it is left
        var keyMembers = entityType.FindPrimaryKey()?.Properties
            .Where(p => !p.IsShadowProperty())
            .Select(p => p.Name)
            .ToArray() ?? [];

        var ordered = string.IsNullOrWhiteSpace(options.OrderBy) && keyMembers.Length > 0
            ? options with { OrderBy = string.Join(", ", keyMembers) }
            : options;

        return ordered.ApplyCriteriaTo(query, keyMembers.FirstOrDefault() ?? "");
    }

    private static async Task<DocumentQueryResult> queryAsync<TEntity>(DbContext dbContext, IEntityType entityType,
        DocumentQueryOptions options, string? tenantId, int pageNumber, int pageSize, CancellationToken token)
        where TEntity : class
    {
        var query = buildQuery<TEntity>(dbContext, entityType, options);

        try
        {
            var total = await query.LongCountAsync(token).ConfigureAwait(false);
            var rows = await query.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(token)
                .ConfigureAwait(false);

            var documents = rows.Select(r => toDocument(entityType, r, tenantId)).ToList();
            return new DocumentQueryResult(documents, total, pageNumber, pageSize);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    private static async Task<StoredDocument?> loadAsync<TEntity>(DbContext dbContext, IEntityType entityType,
        string id, string? tenantId, CancellationToken token) where TEntity : class
    {
        var key = singleKey(entityType)!;
        if (!tryConvertId(id, key.ClrType, out var typedId))
        {
            return null;
        }

        var row = await dbContext.Set<TEntity>().AsNoTracking().Where(keyEquals<TEntity>(key, typedId!))
            .FirstOrDefaultAsync(token).ConfigureAwait(false);

        return row == null ? null : toDocument(entityType, row, tenantId);
    }

    private static Task<DocumentQueryPlan?> explainAsync<TEntity>(DbContext dbContext, IEntityType entityType,
        DocumentQueryOptions options, int pageNumber, int pageSize) where TEntity : class
    {
        var query = buildQuery<TEntity>(dbContext, entityType, options)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize);

        try
        {
            // The SQL EF Core generates for the page. Not every provider can say (InMemory has no SQL), and a
            // provider's own plan needs the statement's parameters bound, which ToQueryString only comments
            var sql = query.ToQueryString();
            var engine = dbContext.Database.ProviderName ?? "EntityFrameworkCore";
            return Task.FromResult<DocumentQueryPlan?>(new DocumentQueryPlan(engine, sql));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult<DocumentQueryPlan?>(null);
        }
        catch (Exception e) when (DocumentQueryCriteria.IsTranslationFailure(e))
        {
            throw options.Untranslatable(e);
        }
    }

    // e => EF.Property<K>(e, "Key") == box.Value, with the id behind a closure member so EF parameterizes it
    private static Expression<Func<TEntity, bool>> keyEquals<TEntity>(IProperty key, object id)
    {
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var property = Expression.Call(typeof(EF), nameof(EF.Property), [key.ClrType], parameter,
            Expression.Constant(key.Name));

        var boxType = typeof(IdBox<>).MakeGenericType(key.ClrType);
        var box = Activator.CreateInstance(boxType, id)!;
        var value = Expression.Property(Expression.Constant(box), nameof(IdBox<int>.Value));

        return Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(property, value), parameter);
    }

    private static bool tryConvertId(string text, Type keyType, out object? value)
    {
        value = null;
        var type = Nullable.GetUnderlyingType(keyType) ?? keyType;
        try
        {
            if (type == typeof(string))
            {
                value = text;
            }
            else if (type == typeof(Guid))
            {
                if (!Guid.TryParse(text, out var guid)) return false;
                value = guid;
            }
            else if (type.IsEnum)
            {
                if (!Enum.TryParse(type, text, true, out var parsed)) return false;
                value = parsed;
            }
            else
            {
                value = Convert.ChangeType(text, type, System.Globalization.CultureInfo.InvariantCulture);
            }

            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    // Scalar properties only, by the property's CLR name: never a navigation, so nothing can cycle or load lazily
    private static StoredDocument toDocument(IEntityType entityType, object entity, string? tenantId)
    {
        var clr = entity.GetType();
        var json = new Dictionary<string, JsonElement>();
        foreach (var property in entityTypeOf(entityType, clr).GetProperties())
        {
            if (property.IsShadowProperty()) continue;

            var member = property.PropertyInfo as MemberInfo ?? property.FieldInfo;
            var value = member switch
            {
                PropertyInfo p => p.GetValue(entity),
                FieldInfo f => f.GetValue(entity),
                _ => null
            };

            json[property.Name] = JsonSerializer.SerializeToElement(value, property.ClrType);
        }

        var id = singleKey(entityTypeOf(entityType, clr)) is { } key
            ? Convert.ToString(
                (key.PropertyInfo as PropertyInfo)?.GetValue(entity) ?? (key.FieldInfo)?.GetValue(entity),
                System.Globalization.CultureInfo.InvariantCulture) ?? ""
            : "";

        return new StoredDocument(id, JsonSerializer.Serialize(json))
        {
            TenantId = tenantId,
            DocumentType = clr.FullNameInCode()
        };
    }

    // The metadata for the row's actual type, so a derived row in a hierarchy serializes its own columns too
    private static IEntityType entityTypeOf(IEntityType requested, Type rowType)
    {
        if (rowType == requested.ClrType) return requested;
        return requested.GetDerivedTypesInclusive().FirstOrDefault(x => x.ClrType == rowType) ?? requested;
    }
}

internal sealed class IdBox<TKey>
{
    public IdBox(TKey value)
    {
        Value = value;
    }

    public TKey Value { get; }
}

internal static class DbContextDocumentDiagnosticsExtensions
{
    // Type.Single semantics without throwing: a short name or table name that two entities share is
    // ambiguous, and an ambiguous name must read as unknown rather than pick one
    public static IEntityType? SingleOrDefaultIfMany(this IEnumerable<IEntityType> types, Func<IEntityType, bool> match)
    {
        var matches = types.Where(match).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

/// <summary>
/// Identifies the entities Wolverine itself maps into a <see cref="DbContext"/> alongside the application's,
/// so a tool browsing <see cref="DbContextDocumentStoreDiagnostics{T}"/> can leave the envelope tables out.
/// </summary>
public static class WolverineMappedEntities
{
    /// <summary>
    /// Whether <paramref name="documentTypeName"/> (as <see cref="DocumentTypeRef.TypeName"/> reports it) is
    /// one of Wolverine's own mapped entities: the inbox's <see cref="IncomingMessage"/> or the outbox's
    /// <see cref="OutgoingMessage"/>.
    /// </summary>
    public static bool IsWolverineEntity(string documentTypeName)
    {
        return documentTypeName == typeof(IncomingMessage).FullNameInCode()
               || documentTypeName == typeof(OutgoingMessage).FullNameInCode();
    }

    /// <inheritdoc cref="IsWolverineEntity(string)"/>
    public static bool IsWolverineEntity(DocumentTypeRef type) => IsWolverineEntity(type.TypeName);

    /// <inheritdoc cref="IsWolverineEntity(string)"/>
    public static bool IsWolverineEntity(Type entityType)
        => entityType == typeof(IncomingMessage) || entityType == typeof(OutgoingMessage);
}
