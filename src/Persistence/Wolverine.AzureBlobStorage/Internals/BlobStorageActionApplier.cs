using Wolverine.Persistence;

namespace Wolverine.AzureBlobStorage.Internals;

/// <summary>
/// What the generated code calls. Public because generated code lives in another assembly.
/// </summary>
public static class BlobStorageActionApplier
{
    public static Task<T?> LoadAsync<T>(IBlobDocumentSession session, object id, string? tenantId,
        CancellationToken token) where T : class
    {
        return session.LoadAsync<T>(id, tenantId, token);
    }

    /// <summary>
    /// Carry out one <see cref="IStorageAction{T}" /> — the <c>Storage.Store()</c> / <c>Insert()</c> /
    /// <c>Update()</c> / <c>Delete()</c> return values, and every element of a <c>UnitOfWork&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Insert, Update and Store all become the same write: an unconditional upload overwrites whatever
    /// is at the blob name, so Blob Storage has no insert-versus-update to honour for a document. These
    /// are last-write-wins by design. A saga is the exception and writes conditionally.
    /// </remarks>
    public static Task ApplyAction<T>(IBlobDocumentSession session, IStorageAction<T> action, string? tenantId,
        CancellationToken token) where T : class
    {
        if (action.Entity == null)
        {
            return Task.CompletedTask;
        }

        return action.Action switch
        {
            StorageAction.Delete => session.DeleteAsync(action.Entity, tenantId, token),
            StorageAction.Insert or StorageAction.Store or StorageAction.Update =>
                session.StoreAsync(action.Entity, tenantId, token),
            _ => Task.CompletedTask
        };
    }

    public static Task StoreAsync<T>(IBlobDocumentSession session, T document, string? tenantId,
        CancellationToken token) where T : class
    {
        return document == null ? Task.CompletedTask : session.StoreAsync(document, tenantId, token);
    }

    public static Task DeleteAsync<T>(IBlobDocumentSession session, T document, string? tenantId,
        CancellationToken token) where T : class
    {
        return document == null ? Task.CompletedTask : session.DeleteAsync(document, tenantId, token);
    }
}
