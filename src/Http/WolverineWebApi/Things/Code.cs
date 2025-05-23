using IntegrationTests;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Marten.Internal;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace WolverineWebApi.Things;

public class Thing
{
    public string Id { get; set; }
    public string Title { get; set; }
}

public class ThingProjection : SingleStreamProjection<Thing>
{

    public static Thing Create(ThingCreated @event) => new() { Id = @event.Id, Title = @event.Title };
    public void Apply(Thing item, ThingTitleUpdated @event) => item.Title = @event.NewTitle;
}



public record ThingTitleUpdated(string Id, string NewTitle);

public record ThingCreated(string Id, string Title);

public interface IThingStore : IDocumentStore;

public class ThingStoreConfiguration: IConfigureMarten<IThingStore>
{
    public void Configure(IServiceProvider services, StoreOptions options)
    {
        options.Connection(Servers.PostgresConnectionString);
        options.DatabaseSchemaName        = "things";

        // Configure the event store to use strings as identifiers to support resource names.
        options.Events.StreamIdentity = StreamIdentity.AsString;

        // Add projections
        options.Projections.Add<ThingProjection>(ProjectionLifecycle.Inline);
    }
}

[MartenStore(typeof(IThingStore))]
public static class ThingEndpoints
{
    [WolverineGet("/things/{thingId}")]
    public static Thing GetThingItemById([Aggregate] Thing item) => item;
    
    public record CreateThingRequest(string Title);
    public record ThingCreationResponse(string Id)
        : CreationResponse("/things/" + Id);
    
    [Transactional]
    [WolverinePost("/things")] 
    public static (ThingCreationResponse, IStartStream) CreateThingItem(CreateThingRequest request)
    {
        var id = Guid.NewGuid().ToString();
        var evt = new ThingCreated(id, request.Title);
        var startStream = MartenOps.StartStream<Thing>(id, evt);
        return (
            new ThingCreationResponse(startStream.StreamKey),
            startStream
        );
    }
    
    
    
}

