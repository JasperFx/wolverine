using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Http.Diagnostics;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

// GH-4182. A query endpoint that returns a COLLECTION of a read model is a view over that read model.
// Before this, EventModelRoles.Describe took the response type verbatim, so the slice's read model
// rendered as the closed generic's assembly-qualified CLR string --
// "System.Collections.Generic.IReadOnlyList`1[[WolverineWebApi.Marten.Order, WolverineWebApi, Version=..."
// -- an unreadable canvas node sitting next to the "Order" node the sibling single-document routes name.
public class event_model_read_model_collection_4182(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public void a_list_response_reads_the_element_type_as_its_read_model()
    {
        // GET /api/orders/list is Get(IQuerySession, CancellationToken) => Task<IReadOnlyList<Order>>
        var chain = HttpChains.ChainFor("GET", "/api/orders/list");
        chain.ShouldNotBeNull();

        var slice = HttpEventModelSource.ForChain(chain);

        slice.Pattern.ShouldBe(SlicePattern.View);
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(Order)]);
    }

    [Fact]
    public void the_read_model_is_the_same_node_the_single_document_route_names()
    {
        // The point of the unwrap: "a list of Order" and "an Order" fold onto ONE canvas node. Asserted
        // on FullName because that is what a viewer keys the node by -- matching on Name alone would
        // pass even if one slice still carried the closed generic
        var list = HttpEventModelSource.ForChain(HttpChains.ChainFor("GET", "/api/orders/list")!);
        var single = HttpEventModelSource.ForChain(HttpChains.ChainFor("GET", "/orders/latest/{id}")!);

        list.ReadModelTypes.Single().FullName
            .ShouldBe(single.ReadModelTypes.Single().FullName);

        list.ReadModelTypes.Single().FullName.ShouldBe(typeof(Order).FullName);
    }

    [Fact]
    public void a_paged_list_response_unwraps_too()
    {
        // GET /api/orders/query returns Marten's IPagedList<Order>, which is not any of the framework
        // collection types -- it only IS one through IReadOnlyList<T>. A page of Order is still a view
        // over Order, which is why the unwrap walks interfaces rather than matching a whitelist
        var chain = HttpChains.ChainFor("GET", "/api/orders/query");
        chain.ShouldNotBeNull();

        var slice = HttpEventModelSource.ForChain(chain);

        slice.Pattern.ShouldBe(SlicePattern.View);
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe([nameof(Order)]);
    }
}
