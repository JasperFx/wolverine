using Wolverine.Persistence.ClaimCheck.Internal;

namespace Wolverine.Persistence;

/// <summary>
/// Fluent configuration entry-point for Wolverine's Claim Check pipeline.
/// Backend packages (Azure Blob Storage, S3, file system, etc.) attach
/// themselves to a <see cref="WolverineOptions"/> through this object.
/// </summary>
public class ClaimCheckConfiguration
{
    private readonly List<ClaimCheckRoute> _routes = new();
    private readonly Dictionary<string, IClaimCheckStore> _namedStores = new();
    private int _routeCounter;

    public ClaimCheckConfiguration(WolverineOptions options)
    {
        Options = options;
    }

    internal IReadOnlyList<ClaimCheckRoute> Routes => _routes;
    internal IReadOnlyDictionary<string, IClaimCheckStore> NamedStores => _namedStores;

    private ClaimCheckConfiguration addRoute(string key, IClaimCheckStore store,
        Func<Type, bool>? typeMatch, Func<Envelope, bool>? envelopeMatch, long? threshold)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        _namedStores[key] = store;
        _routes.Add(new ClaimCheckRoute(key, typeMatch, envelopeMatch, threshold));
        return this;
    }

    /// <summary>
    /// Route messages of exactly type <typeparamref name="T"/> to <paramref name="store"/>, overriding
    /// the global <see cref="Store"/>. Optionally override the whole-body auto-offload threshold for that
    /// message. Routes are evaluated in registration order; the first match wins. The store choice is
    /// stamped onto the outgoing envelope so the receiver loads from the same backend. See GH-3508.
    /// </summary>
    public ClaimCheckConfiguration StoreForMessage<T>(IClaimCheckStore store, long? autoOffloadThreshold = null)
        => addRoute("msg:" + typeof(T).FullName, store, t => t == typeof(T), null, autoOffloadThreshold);

    /// <summary>
    /// Route every outgoing message whose type matches <paramref name="predicate"/> to
    /// <paramref name="store"/>. Because the store key for a predicate route is positional, the receiving
    /// host must register the same predicate routes in the same order (normal for a shared configuration).
    /// Prefer <see cref="StoreForMessage{T}"/> when you can — its key is derived from the message type and
    /// is order-independent. See GH-3508.
    /// </summary>
    public ClaimCheckConfiguration StoreForMessages(Func<Type, bool> predicate, IClaimCheckStore store,
        long? autoOffloadThreshold = null)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return addRoute("route:" + _routeCounter++, store, predicate, null, autoOffloadThreshold);
    }

    /// <summary>
    /// Route every outgoing message whose envelope matches <paramref name="predicate"/> (e.g. by
    /// destination endpoint) to <paramref name="store"/>. Evaluated only on the send side; the selected
    /// store key travels with the message, so the receiver resolves the same store even though the
    /// receiving endpoint URI differs. Positional key — see <see cref="StoreForMessages"/> on ordering.
    /// See GH-3508.
    /// </summary>
    public ClaimCheckConfiguration StoreWhen(Func<Envelope, bool> predicate, IClaimCheckStore store,
        long? autoOffloadThreshold = null)
    {
        if (predicate is null) throw new ArgumentNullException(nameof(predicate));
        return addRoute("route:" + _routeCounter++, store, null, predicate, autoOffloadThreshold);
    }

    /// <summary>
    /// The owning <see cref="WolverineOptions"/> instance.
    /// </summary>
    public WolverineOptions Options { get; }

    /// <summary>
    /// When set, any outgoing message whose serialized body exceeds this size (in bytes) is
    /// automatically off-loaded to the <see cref="Store"/> in full and replaced on the wire with a
    /// single reference header — even when no property carries a <see cref="BlobAttribute"/>. This is
    /// the safety net for the "forgot [Blob] on a big property and slammed into the broker's size
    /// limit" failure. Null (the default) disables auto-offload; the <c>[Blob]</c> attribute remains
    /// the explicit, opt-in per-property path. See GH-3504.
    /// </summary>
    public long? AutoOffloadThreshold { get; set; }

    /// <summary>
    /// Automatically off-load any outgoing message whose serialized body exceeds
    /// <paramref name="thresholdInBytes"/>, without requiring a <see cref="BlobAttribute"/>.
    /// </summary>
    /// <param name="thresholdInBytes">Body size, in bytes, above which the whole body is off-loaded.</param>
    public ClaimCheckConfiguration AutoOffloadPayloadsLargerThan(long thresholdInBytes)
    {
        if (thresholdInBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdInBytes),
                "The auto-offload threshold must be a positive number of bytes.");
        }

        AutoOffloadThreshold = thresholdInBytes;
        return this;
    }

    /// <summary>
    /// The active claim-check store. If left null when
    /// <see cref="WolverineOptionsClaimCheckExtensions.UseClaimCheck"/> finishes,
    /// a <see cref="FileSystemClaimCheckStore"/> rooted at <c>Path.GetTempPath()/wolverine-claim-check</c>
    /// will be created automatically.
    /// </summary>
    public IClaimCheckStore? Store { get; set; }

    /// <summary>
    /// Configure the pipeline to use a directory-backed
    /// <see cref="FileSystemClaimCheckStore"/>. Pass <c>null</c> to use the default
    /// location under the system temp folder.
    /// </summary>
    public ClaimCheckConfiguration UseFileSystem(string? directory = null)
    {
        Store = new FileSystemClaimCheckStore(directory ?? DefaultFileSystemDirectory());
        return this;
    }

    internal static string DefaultFileSystemDirectory()
        => Path.Combine(Path.GetTempPath(), "wolverine-claim-check");
}
