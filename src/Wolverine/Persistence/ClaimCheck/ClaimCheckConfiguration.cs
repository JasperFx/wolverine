namespace Wolverine.Persistence;

/// <summary>
/// Fluent configuration entry-point for Wolverine's Claim Check pipeline.
/// Backend packages (Azure Blob Storage, S3, file system, etc.) attach
/// themselves to a <see cref="WolverineOptions"/> through this object.
/// </summary>
public class ClaimCheckConfiguration
{
    public ClaimCheckConfiguration(WolverineOptions options)
    {
        Options = options;
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
