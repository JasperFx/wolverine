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

    /// <summary>
    /// GH-5983. Opt in to answering a missing event stream on an HTTP endpoint the same way whatever expected version
    /// the request sends. An aggregate handler workflow endpoint with an expected version (<c>VersionSource</c>, or the
    /// <c>version</c> convention) passes the version to <c>FetchForWriting(id, version)</c>, which throws a concurrency
    /// failure for a stream that does not exist before the endpoint can answer 404. With this on, an HTTP endpoint loads
    /// the stream without the version and checks it after the missing aggregate handling, so a missing stream answers
    /// 404 (or what <see cref="OnMissing" /> asks for) and only an existing stream at another version is a concurrency
    /// failure. Message handlers are unaffected. Off by default in 6.x; this is the behavior of Wolverine 7.0.
    /// </summary>
    public bool CheckExpectedVersionAfterMissingAggregate { get; set; }
}
