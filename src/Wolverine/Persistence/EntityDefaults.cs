namespace Wolverine.Persistence;

/// <summary>
/// Global default settings for entity loading behavior across all [Entity], [Document],
/// [Aggregate], [ReadAggregate], and [WriteAggregate] attributes. Individual attribute
/// settings always take precedence over these defaults.
/// </summary>
public class EntityDefaults
{
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
}
