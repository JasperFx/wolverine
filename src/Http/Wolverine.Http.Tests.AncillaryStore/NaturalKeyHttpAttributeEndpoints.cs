using JasperFx.Events.Aggregation;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.Marten;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Http.Tests.AncillaryStore;

// Every attribute that loads an aggregate, by a natural key from the route, the query string, the request
// body or an [AsParameters] object, and an Async view by its mapped key. Registered on the same ancillary
// store as NkGadget, for the reason given there.

/// <summary>An Async view of the <see cref="NkGadget" /> streams, found through the gadget's lookup.</summary>
public class NkGadgetView
{
    public Guid Id { get; set; }

    [MappedNaturalKey<NkGadget>]
    public GadgetCode Code { get; set; }

    public string Name { get; set; } = string.Empty;

    public void Apply(NkGadgetCreated e)
    {
        Code = e.Code;
        Name = e.Name;
    }

    public void Apply(NkGadgetRenamed e) => Name = e.Name;
}

public record RenameNkGadgetByCode(GadgetCode Code, string Name);

public record RenameNkGadgetAtVersion(string Name, long Version);

public record NkGadgetRenameArgs([FromRoute] GadgetCode Code, [FromBody] RenameNkGadget Body);

public record GadgetAndBin(string Gadget, int Bin);

[MartenStore(typeof(INkThingStore))]
public static class NkHttpAttributeEndpoints
{
    // ---------- [WriteModel] and [ReadModel] ----------

    [WolverinePost("/nkhttp/write-model/{code}")]
    [EmptyResponse]
    public static NkGadgetRenamed WriteModelByRoute(RenameNkGadget command,
        [WriteModel<GadgetCode>("code")] NkGadget gadget) => new(command.Name);

    [WolverinePost("/nkhttp/write-model-by-body")]
    [EmptyResponse]
    public static NkGadgetRenamed WriteModelByBody(RenameNkGadgetByCode command, [WriteModel] NkGadget gadget)
        => new(command.Name);

    [WolverineGet("/nkhttp/read-model/{code}")]
    public static NkGadget ReadModelByRoute([ReadModel<GadgetCode>("code")] NkGadget gadget) => gadget;

    [WolverineGet("/nkhttp/read-model-by-query")]
    public static NkGadget ReadModelByQuery(GadgetCode code, [ReadModel("code")] NkGadget gadget) => gadget;

    #region sample_wolverine_http_read_aggregate_by_natural_key_query
    // A query string value is only seen through a parameter of the endpoint
    [WolverineGet("/nkhttp/read-aggregate-by-query")]
    public static NkGadget ReadAggregateByQuery(GadgetCode code, [ReadAggregate<GadgetCode>("code")] NkGadget gadget)
        => gadget;
    #endregion

    // ---------- the HTTP [Aggregate] and [ConsistentAggregate] ----------

    [WolverinePost("/nkhttp/aggregate-by-body")]
    [EmptyResponse]
    public static NkGadgetRenamed AggregateByBody(RenameNkGadgetByCode command, [Aggregate] NkGadget gadget)
        => new(command.Name);

    #region sample_wolverine_http_aggregate_with_natural_key_property
    // NaturalKey = typeof(TKey) is the spelling to use from F#
    [WolverinePost("/nkhttp/aggregate/{code}")]
    [EmptyResponse]
    public static NkGadgetRenamed AggregateByRoute(RenameNkGadget command,
        [Aggregate("code", NaturalKey = typeof(GadgetCode))] NkGadget gadget) => new(command.Name);
    #endregion

    [WolverinePost("/nkhttp/consistent/{code}")]
    [EmptyResponse]
    public static NkGadgetRenamed ConsistentByRoute(RenameNkGadget command,
        [Wolverine.Http.Marten.ConsistentAggregate("code", NaturalKey = typeof(GadgetCode))] NkGadget gadget)
        => new(command.Name);

    // ---------- [AsParameters] ----------

    [WolverinePost("/nkhttp/as-parameters/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameAsParameters([Microsoft.AspNetCore.Http.AsParameters] NkGadgetRenameArgs args,
        [WriteAggregate<GadgetCode>("code")] NkGadget gadget) => new(args.Body.Name);

    // ---------- a mapped key ----------

    [WolverinePost("/nkhttp/view/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameView(RenameNkGadget command,
        [WriteAggregate<GadgetCode>("code")] NkGadgetView view) => new(command.Name);

    [WolverineGet("/nkhttp/view/{code}")]
    public static NkGadgetView GetView([ReadAggregate<GadgetCode>("code")] NkGadgetView view) => view;

    // ---------- at a version, exclusively, and beside another load ----------

    [WolverinePost("/nkhttp/versioned/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameAtVersion(RenameNkGadgetAtVersion command,
        [WriteAggregate<GadgetCode>("code")] NkGadget gadget) => new(command.Name);

    [WolverinePost("/nkhttp/exclusive/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameExclusively(RenameNkGadget command,
        [WriteAggregate<GadgetCode>("code", LoadStyle = ConcurrencyStyle.Exclusive)] NkGadget gadget)
        => new(command.Name);

    [WolverineGet("/nkhttp/gadget/{code}/bin/{number}")]
    public static GadgetAndBin GetGadgetAndBin([ReadAggregate<GadgetCode>("code")] NkGadget gadget,
        [ReadAggregate<BinNumber>("number")] NkBin bin) => new(gadget.Name, bin.Number.Value);
}
