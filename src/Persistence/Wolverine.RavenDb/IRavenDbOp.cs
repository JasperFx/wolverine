using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb;

public interface IRavenDbOp : ISideEffect
{
    Task Execute(IAsyncDocumentSession session);
}