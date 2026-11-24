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

    // ---------- GH-5954: inferring the natural key from the route ----------

    #region sample_wolverine_http_read_aggregate_by_inferred_natural_key
    // {code} is named like NkGadget's [NaturalKey] Code, so it's read as a GadgetCode and the gadget is loaded by its
    // natural key. No key type or route value name needed on the attribute
    [WolverineGet("/nkhttp/inferred/{code}")]
    public static NkGadget GetByInferredKey([ReadAggregate] NkGadget gadget) => gadget;
    #endregion

    // Named in the attribute, still with no key type
    [WolverinePost("/nkhttp/inferred/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByInferredKey(RenameNkGadget command, [WriteAggregate("code")] NkGadget gadget)
        => new(command.Name);

    #region sample_wolverine_http_read_aggregate_by_inferred_natural_key_query
    // No route value and no endpoint parameter: ?code= is read as NkGadget's Code natural key
    [WolverineGet("/nkhttp/inferred-query")]
    public static NkGadget GetByInferredQueryKey([ReadAggregate] NkGadget gadget) => gadget;
    #endregion

    // Named in the attribute, and only in the query string
    [WolverinePost("/nkhttp/inferred-query/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByInferredQueryKey(RenameNkGadget command,
        [WriteAggregate("code")] NkGadget gadget) => new(command.Name);

    // Named like the key type rather than the key member
    [WolverinePost("/nkhttp/inferred-by-type/{gadgetCode}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByKeyTypeName(RenameNkGadget command, [WriteAggregate] NkGadget gadget)
        => new(command.Name);

    // A guid route constraint keeps {code} a stream id
    [WolverinePost("/nkhttp/constrained/{code:guid}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByConstrainedStreamId(RenameNkGadget command,
        [WriteAggregate("code")] NkGadget gadget) => new(command.Name);

    // So does declaring it as a Guid parameter of the endpoint
    [WolverinePost("/nkhttp/declared/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByDeclaredStreamId(RenameNkGadget command, Guid code,
        [WriteAggregate("code")] NkGadget gadget) => new(command.Name);

    // Named like nothing the gadget has: still a stream id, with a warning at code generation
    [WolverinePost("/nkhttp/unmatched/{number}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByUnmatchedName(RenameNkGadget command, [WriteAggregate("number")] NkGadget gadget)
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
