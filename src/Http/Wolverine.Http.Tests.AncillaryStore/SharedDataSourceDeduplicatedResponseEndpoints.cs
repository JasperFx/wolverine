using System.Collections.Concurrent;
using Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.AncillaryStore;

// GH-5160. [DeduplicatedWithResponse(ClaimInTransaction = true)] on the main store and on an ancillary store that
// were both handed ONE NpgsqlDataSource, each with its own message schema. Here rather than in Wolverine.Http.Tests
// because the ancillary endpoint needs INkThingStore registered; see the csproj.

public record SharedSourceOrder(string Name);

public record SharedSourceOrderCreated(Guid Id);

public class SharedSourceOrderDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class SharedDataSourceDeduplicatedResponseEndpoints
{
    private static readonly ConcurrentDictionary<string, int> _runs = new();

    public static int Runs(string name) => _runs.GetValueOrDefault(name);

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/shared-source-deduplicated-response/main")]
    public static SharedSourceOrderCreated PostMain(SharedSourceOrder request, IDocumentSession session)
        => store(request, session);

    [MartenStore(typeof(INkThingStore))]
    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/shared-source-deduplicated-response/ancillary")]
    public static SharedSourceOrderCreated PostAncillary(SharedSourceOrder request, IDocumentSession session)
        => store(request, session);

    private static SharedSourceOrderCreated store(SharedSourceOrder request, IDocumentSession session)
    {
        _runs.AddOrUpdate(request.Name, 1, (_, count) => count + 1);
        var document = new SharedSourceOrderDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new SharedSourceOrderCreated(document.Id);
    }
}
