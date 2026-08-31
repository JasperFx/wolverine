namespace Wolverine.AzureBlobStorage;

/// <summary>
/// Reads and writes registered document types as Azure blobs.
/// </summary>
/// <remarks>
/// Not a unit of work. Blob Storage has no transaction spanning blobs, so every method here takes
/// effect immediately: a handler that writes two documents and then throws has written one of them.
/// </remarks>
public interface IBlobDocumentSession
{
    /// <summary>
    /// Load a document by its identity, or null when the blob does not exist.
    /// </summary>
    Task<T?> LoadAsync<T>(object id, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Write a document, overwriting whatever is at its blob name.
    /// </summary>
    /// <remarks>
    /// A type registered through <c>Saga&lt;T&gt;()</c> is the exception: its write is conditional and
    /// throws <see cref="SagaConcurrencyException" /> when another message changed it first.
    /// </remarks>
    Task StoreAsync<T>(T document, string? tenantId, CancellationToken token = default) where T : class;

    /// <summary>
    /// Delete the blob a document lives at. Missing blobs are not an error.
    /// </summary>
    Task DeleteAsync<T>(T document, string? tenantId, CancellationToken token = default) where T : class;

    Task DeleteByIdAsync<T>(object id, string? tenantId, CancellationToken token = default) where T : class;
}
