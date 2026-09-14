using JasperFx.Events.Aggregation;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Http.Tests.AncillaryStore;

// GH-4439. An aggregate identified by a natural key, registered ONLY on an ancillary store, reached over
// HTTP. The endpoints are worth testing separately from the message handlers because the two chain types
// resolve their store at opposite points: a handler chain is rescued by the Phase-A eager policies that
// pre-assign IChain.AncillaryStoreType, while an HTTP chain does parameter matching inside HttpChain's
// constructor -- before [MartenStore] has been applied and with no eager policy in sight, since those are
// IHandlerPolicy. Only chain.DetermineAncillaryStoreType() answers correctly there.
//
// See the csproj for why these live in an assembly of their own.

public interface INkThingStore : global::Marten.IDocumentStore;

public record ThingCode(string Value);

public class NkThing
{
    public Guid Id { get; set; }

    [NaturalKey]
    public ThingCode Code { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public bool IsArchived { get; set; }

    [NaturalKeySource]
    public void Apply(NkThingCreated e)
    {
        Code = e.Code;
        Title = e.Title;
    }

    public void Apply(NkThingRenamed e)
    {
        Title = e.NewTitle;
    }

    public void Apply(NkThingArchived e)
    {
        IsArchived = true;
    }
}

public record NkThingCreated(ThingCode Code, string Title);
public record NkThingRenamed(string NewTitle);
public record NkThingArchived;

public record RenameNkThing(ThingCode Code, string NewTitle);
public record ArchiveNkThing(ThingCode Code);

[MartenStore(typeof(INkThingStore))]
public static class NkThingEndpoints
{
    // [EmptyResponse] is load-bearing rather than decoration: it is what tells Wolverine.Http the returned
    // event is an event for the aggregate workflow to append instead of this endpoint's HTTP resource
    // (HttpChain keys off the attribute to set NoContent). Without it the endpoint answers 200 with an empty
    // body and silently appends nothing -- the aggregate is fetched, the handler runs, and no event is
    // written. Same shape as /orders/ship-with-body-version and /sti/incrementa in the sample app.
    [WolverinePost("/nkthings/rename")]
    [EmptyResponse]
    public static NkThingRenamed Rename(RenameNkThing command, [WriteAggregate] NkThing thing)
    {
        return new NkThingRenamed(command.NewTitle);
    }

    // The store-agnostic spelling, which resolves its provider through the registered persistence
    // strategies rather than naming Marten.
    [WolverinePost("/nkthings/archive")]
    [EmptyResponse]
    public static NkThingArchived Archive(ArchiveNkThing command, [WriteModel] NkThing thing)
    {
        return new NkThingArchived();
    }
}
