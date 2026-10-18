using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Catalog;

public static class DeleteProductEndpoint
{
    [WolverineDelete("/products/{id}")]
    public static void Delete(Guid id, [Entity(Required = true)] Product product, IDocumentSession session)
    {
        session.Delete(product);
    }
}
