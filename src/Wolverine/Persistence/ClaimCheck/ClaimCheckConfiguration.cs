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
