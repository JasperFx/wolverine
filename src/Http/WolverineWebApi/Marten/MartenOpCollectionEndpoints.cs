using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

public class SideEffectNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

// GH-5684. A collection of Marten side effects returned from an HTTP endpoint is executed, like it is
// from a message handler, and is never written out as the response body
public static class MartenOpCollectionEndpoints
{
    [WolverinePost("/marten-ops/list/{id}")]
    public static List<IMartenOp> PostList(Guid id)
    {
        return [MartenOps.Store(new SideEffectNote { Id = id, Text = "list" })];
    }

    [WolverinePost("/marten-ops/enumerable/{id}")]
    public static IEnumerable<IMartenOp> PostEnumerable(Guid id)
    {
        yield return MartenOps.Store(new SideEffectNote { Id = id, Text = "enumerable" });
    }

    [WolverinePost("/marten-ops/array/{id}")]
    public static IMartenOp[] PostArray(Guid id)
    {
        return [MartenOps.Store(new SideEffectNote { Id = id, Text = "array" })];
    }
}
