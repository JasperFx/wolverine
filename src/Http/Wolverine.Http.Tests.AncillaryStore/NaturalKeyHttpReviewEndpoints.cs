using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.AncillaryStore;

// GH-5947 and GH-5948, from the natural keys API review (#5897): the generic [Aggregate<TKey>], a 400 for a write with
// no identity, and the decider workflow loading by a natural key in the request body. Registered on the same ancillary
// store as NkGadget, for the reason given there.

public record RenameNkGadgetById(Guid Id, string Name);

[MartenStore(typeof(INkThingStore))]
public static class NkHttpReviewEndpoints
{
    #region sample_wolverine_http_aggregate_by_natural_key_type_argument
    // [Aggregate<TKey>] is [Aggregate(NaturalKey = typeof(TKey))]
    [WolverinePost("/nkhttp/aggregate-generic/{code}")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByGenericAggregate(RenameNkGadget command,
        [Aggregate<GadgetCode>("code")] NkGadget gadget) => new(command.Name);
    #endregion

    // A stream id route value named like one: no warning, though NkGadget has a natural key
    [WolverinePost("/nkhttp/by-stream-id/{id}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByStreamIdInRoute(RenameNkGadget command, [WriteAggregate("id")] NkGadget gadget)
        => new(command.Name);

    [WolverinePost("/nkhttp/by-stream-id-in-body")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByStreamIdInBody(RenameNkGadgetById command, [WriteAggregate] NkGadget gadget)
        => new(command.Name);

    #region sample_wolverine_http_aggregate_handler_by_natural_key
    // The request body has no stream id, so the gadget is loaded by the GadgetCode in it
    [WolverinePost("/nkhttp/decider")]
    [EmptyResponse]
    [AggregateHandler]
    public static NkGadgetRenamed RenameByDecider(RenameNkGadgetByCode command, NkGadget gadget)
        => new(command.Name);
    #endregion
}
