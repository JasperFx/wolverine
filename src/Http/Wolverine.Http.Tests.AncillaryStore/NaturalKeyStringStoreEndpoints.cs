using JasperFx.Events.Aggregation;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.AncillaryStore;

// Natural keys from the route on a store whose streams are identified by string keys. Without the key type a
// route value is the stream key, which any string parses into. With it, the value is the natural key. The key
// is a wrapper: a bare string key on a string-identity store is refused when the store is built.
//
// Every host that scans this assembly builds these endpoints eagerly (see the csproj), so each of them
// registers this store too.

public interface INkStringStore : global::Marten.IDocumentStore;

public class NkStringGadget
{
    public string Id { get; set; } = string.Empty;

    [NaturalKey]
    public GadgetCode Code { get; set; }

    public string Name { get; set; } = string.Empty;

    [NaturalKeySource]
    public void Apply(NkStringGadgetCreated e)
    {
        Code = e.Code;
        Name = e.Name;
    }

    public void Apply(NkStringGadgetRenamed e) => Name = e.Name;
}

public record NkStringGadgetCreated(GadgetCode Code, string Name);
public record NkStringGadgetRenamed(string Name);

public record RenameNkStringGadget(string Name);

[MartenStore(typeof(INkStringStore))]
public static class NkStringStoreEndpoints
{
    [WolverinePost("/nkstring-gadgets/{code}/rename")]
    [EmptyResponse]
    public static NkStringGadgetRenamed Rename(RenameNkStringGadget command,
        [WriteAggregate<GadgetCode>("code")] NkStringGadget gadget)
        => new(command.Name);

    [WolverineGet("/nkstring-gadgets/{code}")]
    public static NkStringGadget Get([ReadAggregate<GadgetCode>("code")] NkStringGadget gadget) => gadget;
}
