using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb;

public interface IRavenDbOp : ISideEffect
{
    Task Execute(IAsyncDocumentSession session);
}

public class StoreDoc<T>(T Document) : IRavenDbOp
{
    public Task Execute(IAsyncDocumentSession session)
    {
        return session.StoreAsync(Document);
    }
}

public class DeleteByDoc(object Document) : IRavenDbOp
{
    public Task Execute(IAsyncDocumentSession session)
    {
        session.Delete(Document);
        return Task.CompletedTask;
    }
}

public class DeleteById(string Id) : IRavenDbOp
{
    public Task Execute(IAsyncDocumentSession session)
    {
        session.Delete(Id);
        return Task.CompletedTask;
    }
}

#region sample_ravenops

/// <summary>
/// Side effect helper class for Wolverine's integration with RavenDb
/// </summary>
public static class RavenOps
{
    /// <summary>
    /// Store a new RavenDb document
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IRavenDbOp Store<T>(T document) => new StoreDoc<T>(document);

    /// <summary>
    /// Delete this document in RavenDb
    /// </summary>
    /// <param name="document"></param>
    /// <returns></returns>
    public static IRavenDbOp DeleteDocument(object document) => new DeleteByDoc(document);

    /// <summary>
    /// Delete a RavenDb document by its id
    /// </summary>
    /// <param name="id"></param>
    /// <returns></returns>
    public static IRavenDbOp DeleteById(string id) => new DeleteById(id);
}

#endregion