using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Wolverine.Configuration;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel4975;

// GH-4975 (jasperfx#1003): a read-only chain is a Query slice that READS its read model, aggregate or document,
// not a View claiming to produce it. The View producing the read model stays its own slice; the read is what
// links the two.
public class event_model_query_slices_4975
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    private static EventModelSliceDescriptor describeGet(EventModelSliceSeed seed)
        => EventModelRoles.Describe(chainFor<InvoiceQueryHandler>(x => InvoiceQueryHandler.Handle(null!)), seed);

    [Fact]
    public void a_read_only_trigger_is_a_query_that_reads_its_response()
    {
        var slice = describeGet(new EventModelSliceSeed("GET /invoices/{id}", TriggerKind.Http, null, null,
            typeof(InvoiceQueryHandler))
        {
            ResponseType = typeof(Invoice),
            FirstReturnValueIsResponse = true,
            IsQuery = true
        });

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.ReadsFrom.Select(x => x.Name).ShouldBe(new[] { nameof(Invoice) });
        slice.ReadModelTypes.ShouldBeEmpty();
        slice.PatternViolations.ShouldBeEmpty();
    }

    [Fact]
    public void a_collection_response_reads_its_element_type()
    {
        var slice = describeGet(new EventModelSliceSeed("GET /invoices", TriggerKind.Http, null, null,
            typeof(InvoiceQueryHandler))
        {
            ResponseType = typeof(Task<IReadOnlyList<Invoice>>),
            FirstReturnValueIsResponse = true,
            IsQuery = true
        });

        slice.ReadsFrom.Select(x => x.FullName).ShouldBe(new[] { typeof(Invoice).FullName! });
    }

    [Fact]
    public void a_handler_declared_a_query_reads_the_reply_it_returns()
    {
        // [SlicePattern(SlicePattern.Query)]: InvokeAsync<Invoice>'s reply is read, not a cascaded message
        var slice = EventModelRoles.ForHandlerChain(chainFor<InvoiceQueryHandler>(x => InvoiceQueryHandler.Handle(null!)));

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.ReadsFrom.Select(x => x.Name).ShouldBe(new[] { nameof(Invoice) });
        slice.PublishedMessages.ShouldBeEmpty();
        slice.PatternViolations.ShouldBeEmpty();
    }

    [Fact]
    public void a_handler_declared_a_query_that_writes_keeps_its_writes_and_is_flagged()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<LeakyInvoiceQueryHandler>(x => LeakyInvoiceQueryHandler.Handle(null!, null!)));

        slice.Pattern.ShouldBe(SlicePattern.Query);
        slice.PatternViolations.ShouldNotBeEmpty();
        slice.PatternViolations.ShouldAllBe(x => x.Origin == HotspotOrigin.PatternViolation);
    }

    [Fact]
    public void a_grpc_rpc_keeps_a_declared_query()
    {
        // An RPC rules out an Automation, not a read; the declaration is the only evidence gRPC has
        var slice = EventModelRoles.ForHandlerChain(chainFor<InvoiceQueryHandler>(x => InvoiceQueryHandler.Handle(null!)));

        var manifest = new StubGrpcManifest(new GrpcEndpointDescriptor("Invoices", "GetInvoice",
            typeof(GetInvoice), typeof(Invoice), typeof(InvoiceQueryHandler),
            GrpcServiceDiscoveryMode.CodeFirst, GrpcRpcStreamKind.Unary));

        var applied = WolverineEventModelSource.ApplyGrpcTriggers(new EventModelDescriptor("app", new[] { slice }), manifest);

        applied.Slices.Single().Pattern.ShouldBe(SlicePattern.Query);
        applied.Slices.Single().TriggerKind.ShouldBe(TriggerKind.Grpc);
    }

    [Fact]
    public void a_declared_query_joins_the_derived_one_on_what_it_reads_and_not_the_view()
    {
        // The board says "GetInvoice reads Invoice" and "InvoiceSummary produces Invoice"; the code has a GET
        // route named for its verb and route. The query joins the query; the view is left alone.
        var board = new EventModelBuilder();
        board.Query<Invoice>();
        board.View<Invoice>().From<InvoiceIssued>();
        var declared = board.Build("app").WithProvenance(EventModelProvenance.Declared);

        var derived = new EventModelDescriptor("app", new[]
        {
            describeGet(new EventModelSliceSeed("GET /invoices/{id}", TriggerKind.Http, null, null, typeof(InvoiceQueryHandler))
            {
                ResponseType = typeof(Invoice),
                FirstReturnValueIsResponse = true,
                IsQuery = true
            })
        }).WithProvenance(EventModelProvenance.Derived);

        var aligned = EventModelSliceAlignment.AlignSliceNames(new[] { declared, derived });

        aligned[1].Slices.Single().Name.ShouldBe("GetInvoice");

        var merged = EventModelDescriptor.Merge("app", aligned);
        merged.Slices.Select(x => x.Name).OrderBy(x => x).ShouldBe(new[] { "GetInvoice", nameof(Invoice) });
        merged.Slices.Single(x => x.Name == "GetInvoice").Pattern.ShouldBe(SlicePattern.Query);
        merged.Slices.Single(x => x.Name == nameof(Invoice)).Pattern.ShouldBe(SlicePattern.View);

        // ...and the read links the query to the view that produces what it reads
        merged.Links.ShouldContain(x => x.Kind == EventModelLinkKind.ReadModelRead && x.FromSlice == nameof(Invoice) &&
                                        x.ToSlice == "GetInvoice");
    }

    [Fact]
    public void the_report_lists_what_a_query_reads_and_its_pattern_violations()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<LeakyInvoiceQueryHandler>(x => LeakyInvoiceQueryHandler.Handle(null!, null!)));
        var report = EventModelReport.Slices(new EventModelDescriptor("app", new[] { slice })).Single();

        report.Hotspots.ShouldContain(x => x.StartsWith(nameof(HotspotOrigin.PatternViolation)));
    }

    private sealed class StubGrpcManifest(params GrpcEndpointDescriptor[] endpoints) : IGrpcEndpointManifest
    {
        public IReadOnlyList<GrpcEndpointDescriptor> Endpoints { get; } = endpoints;
    }
}

#region sample types for GH-4975

public record GetInvoice(Guid Id);

public record Invoice(Guid Id, decimal Amount);

public record InvoiceIssued(Guid Id, decimal Amount);

public record InvoiceViewed(Guid Id);

public class InvoiceQueryHandler
{
    [SlicePattern(SlicePattern.Query)]
    public static Invoice Handle(GetInvoice query) => new(query.Id, 0);
}

public class LeakyInvoiceQueryHandler
{
    // A "query" that appends: the reader keeps the event, and upstream flags the slice
    [SlicePattern(SlicePattern.Query)]
    public static InvoiceViewed Handle(GetInvoice query, [WriteModel] Invoice invoice) => new(query.Id);
}

#endregion
