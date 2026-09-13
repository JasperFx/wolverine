using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance.EventModel4419;

// GH-4419 §2. A slice READS some read models and PRODUCES others, and until now both landed in
// ReadModelTypes. One list cannot say which a given entry is, so the Automation input edge —
// Event → Read Model → ⚙ Command — could not be drawn at all: a link needs a producer at one end and a
// reader at the other.
public class reads_are_separate_from_writes_4419
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void an_entity_parameter_is_read_from_and_a_storage_action_return_is_produced()
    {
        // The issue's acceptance case, verbatim: reads Ledger, produces Invoice.
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ReviseInvoiceHandler>(x => ReviseInvoiceHandler.Handle(null!, null!)));

        slice.ReadsFrom.Select(x => x.Name).ShouldBe(new[] { nameof(Ledger) });
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Invoice) });
    }

    [Fact]
    public void a_type_both_read_and_written_is_named_in_both_lists()
    {
        // Also the issue's acceptance: both, not one or the other. Upstream renders it as a single
        // element rather than two, so naming it twice costs nothing and losing either side costs a link.
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<TouchInvoiceHandler>(x => TouchInvoiceHandler.Handle(null!, null!)));

        slice.ReadsFrom.Select(x => x.Name).ShouldBe(new[] { nameof(Invoice) });
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Invoice) });
    }
}

// GH-4419 §1. FinishModel used to carry a private copy of the cross-slice join. It is now re-based on
// EventModelDescriptor.Links (EventModelLinks.Compute upstream), so the pattern this derives and the arrow
// a viewer draws cannot disagree.
public class automation_is_derived_from_the_model_links_4419
{
    private static EventModelDescriptor modelWith(params EventModelSliceDescriptor[] slices)
        => WolverineEventModelSource.FinishModel(new EventModelDescriptor("app", slices));

    [Fact]
    public void a_slice_whose_trigger_type_is_another_slices_event_is_an_automation()
    {
        // The deliberate widening. The old scan compared only CommandType, so a slice triggered by an
        // event through TriggerType — what a declared "when this happens" arrow contributes to a merged
        // model — stayed a Command. Upstream's join matches CommandType *or* TriggerType, and the link's
        // ToElementId says which end matched.
        var cause = EventModelSliceDescriptor.Named("revise") with
        {
            EmittedEvents = new[] { TypeDescriptor.For(typeof(LedgerRevised)) }
        };

        var effect = EventModelSliceDescriptor.Named("react") with
        {
            TriggerType = TypeDescriptor.For(typeof(LedgerRevised)),
            TriggerKind = TriggerKind.MessageHandler
        };

        modelWith(cause, effect).Slices.Single(x => x.Name == "react")
            .Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void the_classification_and_the_link_are_the_same_fact()
    {
        var cause = EventModelSliceDescriptor.Named("revise") with
        {
            EmittedEvents = new[] { TypeDescriptor.For(typeof(LedgerRevised)) }
        };

        var effect = EventModelSliceDescriptor.Named("react") with
        {
            CommandType = TypeDescriptor.For(typeof(LedgerRevised)),
            TriggerKind = TriggerKind.MessageHandler
        };

        var model = modelWith(cause, effect);

        // Same model, same evidence: the slice is an Automation *because* it is the To end of this link.
        model.Links.ShouldContain(x =>
            x.Kind == EventModelLinkKind.EventTriggers && x.FromSlice == "revise" && x.ToSlice == "react");
        model.Slices.Single(x => x.Name == "react").Pattern.ShouldBe(SlicePattern.Automation);
    }

    [Fact]
    public void a_slice_that_republishes_what_it_handles_still_does_not_promote_itself()
    {
        // Previously guarded by comparing producer names to the slice's own; now it comes for free,
        // because upstream never links a slice to itself.
        var loop = EventModelSliceDescriptor.Named("loop") with
        {
            CommandType = TypeDescriptor.For(typeof(LedgerRevised)),
            EmittedEvents = new[] { TypeDescriptor.For(typeof(LedgerRevised)) },
            TriggerKind = TriggerKind.MessageHandler
        };

        modelWith(loop).Slices.Single().Pattern.ShouldBeNull();
    }
}

// GH-4425. Both derived sources sit on the Derived rung with a hard-coded subject, and neither stamped
// Origin — so a same-rung disagreement between them rendered as "Derived claims X; Derived claims Y",
// which names neither file. Origin records WHICH source produced the slice.
public class the_derived_source_stamps_its_origin_4425
{
    [Fact]
    public async Task every_slice_carries_the_wolverine_subject()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "origin-4425";
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReviseInvoiceHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var model = WolverineEventModelSource.Describe(host.Services.GetRequiredService<WolverineOptions>());

        model.Slices.ShouldNotBeEmpty();
        model.Slices.ShouldAllBe(x => x.Origin == WolverineEventModelSource.SourceSubject);

        // The subject is the one the instance reports, so a reader can match the stamp to its source.
        WolverineEventModelSource.SourceSubject.ShouldBe(new Uri("event-model://wolverine"));

        // ...and the text a consumer actually matches on carries Uri's trailing slash for an
        // authority-based URI. Long-standing rather than introduced here — Subject was always built from
        // this same string — but worth pinning, because the hotspot text a reader sees is this.
        WolverineEventModelSource.SourceSubject.ToString().ShouldBe("event-model://wolverine/");
    }
}

// GH-4424. EventModelDiscovery returns one model per NAME, and the export folded them all into one named
// for the service — losing a name outright, with nothing reporting it.
public class the_export_keeps_every_model_it_assembled_4424
{
    private static async Task<IHost> twoModelHost()
        => await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddEventModel("Overlay", model =>
                {
                    model.InDomain("Sales");
                    model.Slice("ReviseInvoice").TriggeredBy("UI: revise");
                });
            })
            .UseWolverine(opts =>
            {
                opts.ServiceName = "set-4424";
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ReviseInvoiceHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task both_models_survive_with_their_own_names()
    {
        using var host = await twoModelHost();

        var set = await WolverineEventModelExport.AssembleSetAsync(host.Services, "set-4424",
            TestContext.Current.CancellationToken);

        set.ServiceName.ShouldBe("set-4424");
        set.Models.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(new[] { "Overlay", "set-4424" });
        set.IsAmbiguous.ShouldBeTrue();
        set.Sole.ShouldBeNull();
    }

    [Fact]
    public async Task the_collapsing_shim_records_what_it_folded()
    {
        using var host = await twoModelHost();

        // The old behaviour, still available for a wire that cannot carry two — but no longer silent.
        var collapsed = await WolverineEventModelExport.AssembleAsync(host.Services,
            token: TestContext.Current.CancellationToken);

        collapsed.Name.ShouldBe("set-4424");
        collapsed.Hotspots.ShouldContain(x => x.Origin == HotspotOrigin.ModelCollapse);
    }

    [Fact]
    public async Task a_name_selects_a_model_rather_than_renaming_the_fold()
    {
        using var host = await twoModelHost();

        // --name used to rename the fold of everything; it now chooses which model to export.
        var selected = await WolverineEventModelExport.AssembleAsync(host.Services, "Overlay",
            TestContext.Current.CancellationToken);

        selected.Name.ShouldBe("Overlay");
        selected.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.ModelCollapse,
            "nothing was folded, so nothing was lost");
    }

    [Fact]
    public async Task the_capabilities_document_carries_the_set()
    {
        using var host = await twoModelHost();

        var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null,
            TestContext.Current.CancellationToken);

        var set = capabilities.EventModel.ShouldNotBeNull();
        set.Models.Select(x => x.Name).ShouldContain("Overlay");
        set.Models.Select(x => x.Name).ShouldContain("set-4424");
    }
}

#region sample types for GH-4419 / GH-4424 / GH-4425

public record ReviseInvoice(string Id);

public class Ledger
{
    public string Id { get; set; } = null!;
}

public class Invoice
{
    public string Id { get; set; } = null!;
}

public record LedgerRevised(string Id);

public class ReviseInvoiceHandler
{
    // Reads a Ledger, produces an Invoice: the two roles that used to share one list.
    public static IStorageAction<Invoice> Handle(ReviseInvoice command, [Entity] Ledger ledger)
        => Storage.Update(new Invoice { Id = command.Id });
}

public record TouchInvoice(string Id);

public class TouchInvoiceHandler
{
    // Reads AND produces the same type.
    public static IStorageAction<Invoice> Handle(TouchInvoice command, [Entity] Invoice invoice)
        => Storage.Update(invoice);
}

#endregion
