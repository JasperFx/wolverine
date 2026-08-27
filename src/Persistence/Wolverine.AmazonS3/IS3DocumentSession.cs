namespace Wolverine.AmazonS3;

/// <summary>
/// Reads and writes registered document types as S3 objects.
/// </summary>
/// <remarks>
/// Not a unit of work. S3 has no transaction, so every method here takes effect immediately: a handler
/// that writes two documents and then throws has written one of them.
/// </remarks>
public interface IS3DocumentSession
{
    /// <summary>
    /// Load a document by its identity, or null when the object does not exist.
    /// </summary>
    Task<T?> LoadAsync<T>(object id, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Write a document, overwriting whatever is at its key.
    /// </summary>
    Task StoreAsync<T>(T document, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Delete the object a document lives at. Missing objects are not an error.
    /// </summary>
    Task DeleteAsync<T>(T document, string? tenantId, CancellationToken token = default) where T : class;

    Task DeleteByIdAsync<T>(object id, string? tenantId, CancellationToken token = default) where T : class;
}
