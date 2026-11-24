using JasperFx.Events.Aggregation;
using Marten;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.AncillaryStore;

// [WriteAggregate<TKey>] / [ReadAggregate<TKey>] reading a natural key straight from the route or the query
// string. Without the key type, a route value is taken as the stream id, because it parses into any type.
// The key is a value type: Wolverine.Http only parses route and query values into value-type wrappers.
//
// Registered on the same ancillary store as NkThing, because every host that scans this assembly builds
// these endpoints eagerly (see the csproj).

public readonly record struct GadgetCode(string Value);

public class NkGadget
{
    public Guid Id { get; set; }

    [NaturalKey]
    public GadgetCode Code { get; set; }

    public string Name { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(NkGadgetCreated e)
    {
        Code = e.Code;
        Name = e.Name;
    }

    public void Apply(NkGadgetRenamed e) => Name = e.Name;
}

public record NkGadgetCreated(GadgetCode Code, string Name);
public record NkGadgetRenamed(string Name);

public record RenameNkGadget(string Name);
public record CreateNkGadget(string Code, string Name);

[MartenStore(typeof(INkThingStore))]
public static class NkGadgetEndpoints
{
    #region sample_wolverine_http_write_aggregate_by_natural_key_route
    // The route value is parsed into a GadgetCode and the gadget is loaded by its natural key
    [WolverinePost("/nkgadgets/{code}/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed Rename(RenameNkGadget command, [WriteAggregate<GadgetCode>("code")] NkGadget gadget)
        => new(command.Name);
    #endregion

    // No {code} in the route, so the query string supplies it. As for a stream id, a query string value is
    // only seen through a parameter of the endpoint.
    [WolverinePost("/nkgadgets-by-query/rename")]
    [EmptyResponse]
    public static NkGadgetRenamed RenameByQuery(RenameNkGadget command, GadgetCode code,
        [WriteAggregate<GadgetCode>("code")] NkGadget gadget)
        => new(command.Name);

    #region sample_wolverine_http_read_aggregate_by_natural_key_route
    [WolverineGet("/nkgadgets/{code}")]
    public static NkGadget Get([ReadAggregate<GadgetCode>("code")] NkGadget gadget) => gadget;
    #endregion

    // A second stream claiming an existing code is refused at commit
    [WolverinePost("/nkgadgets")]
    [EmptyResponse]
    public static void Create(CreateNkGadget command, IDocumentSession session)
        => session.Events.StartStream<NkGadget>(Guid.NewGuid(),
            new NkGadgetCreated(new GadgetCode(command.Code), command.Name));
}
