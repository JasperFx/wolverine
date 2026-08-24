namespace Wolverine.Persistence;

/// <summary>
/// Global default settings for entity loading behavior across all [Entity], [Document],
/// [Aggregate], [ReadAggregate], and [WriteAggregate] attributes. Individual attribute
/// settings always take precedence over these defaults.
/// </summary>
public class EntityDefaults
{
    private readonly Dictionary<Type, Type> _loaders = new();

    /// <summary>
    /// The default behavior when a required entity is not found. Individual attributes
    /// can override this value. Built-in default is <see cref="OnMissing.Simple404"/>.
    /// </summary>
    public OnMissing OnMissing { get; set; } = OnMissing.Simple404;

    /// <summary>
    /// The default behavior for whether soft-deleted entities should be treated as valid.
    /// If false, soft-deleted entities are treated as missing. Individual attributes
    /// can override this value. Built-in default is true.
    /// </summary>
    public bool MaybeSoftDeleted { get; set; } = true;

    /// <summary>
    /// Load every <c>[Entity]</c> of type <typeparamref name="TEntity" /> by calling a
    /// <c>Load</c> / <c>LoadAsync</c> method on <typeparamref name="TLoader" /> rather than going to
    /// the application's configured persistence. Register an entity type here when it always comes
    /// from the same place — an object store, a cache, an HTTP API — so the handlers reading it need
    /// nothing more than a plain <c>[Entity]</c>.
    /// <para>
    /// <c>[Entity(Loader = typeof(...))]</c> on the parameter itself overrides this.
    /// </para>
    /// </summary>
    public EntityDefaults LoadWith<TEntity, TLoader>() => LoadWith(typeof(TEntity), typeof(TLoader));

    /// <summary>
    /// Non-generic overload of <see cref="LoadWith{TEntity,TLoader}" />.
    /// </summary>
    public EntityDefaults LoadWith(Type entityType, Type loaderType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(loaderType);

        // Fail here rather than at codegen time: this call site names both types, so it is the one
        // place where the mistake is obvious.
        EntityLoaderPlan.For(loaderType, entityType);

        _loaders[entityType] = loaderType;
        return this;
    }

    internal bool TryFindLoader(Type entityType, out Type loaderType)
    {
        return _loaders.TryGetValue(entityType, out loaderType!);
    }
}
