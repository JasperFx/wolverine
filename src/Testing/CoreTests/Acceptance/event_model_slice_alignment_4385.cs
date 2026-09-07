using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence.EventSourcing;
using Xunit;

namespace CoreTests.Acceptance.EventModel4385;

// GH-4385: a declared Event Model names a slice for the behaviour ("ConfirmAppointment"); Wolverine's
// derived sources name it for what they can see (the message type, the triggering event, the route).
// The two were never going to compute the same merge key, so an eleven-slice sample assembled as
// twenty-two slices with zero hotspots -- the provenance ladder never engaged, because the sources
// never met. HandlerType is the role both kinds of source fill and mean the same thing by.
public class event_model_slice_alignment_4385
{
    private static EventModelDescriptor declared(params EventModelSliceDescriptor[] slices)
        => new EventModelDescriptor("board", slices).WithProvenance(EventModelProvenance.Declared);

    private static EventModelDescriptor derived(params EventModelSliceDescriptor[] slices)
        => new EventModelDescriptor("app", slices).WithProvenance(EventModelProvenance.Derived);

    private static EventModelSliceDescriptor slice(string name, Type? handlerType = null)
        => EventModelSliceDescriptor.Named(name) with
        {
            HandlerType = handlerType is null ? null : TypeDescriptor.For(handlerType)
        };

    [Fact]
    public void the_declared_name_wins_where_two_rungs_describe_the_same_handler()
    {
        var aligned = EventModelSliceAlignment.AlignSliceNames(new[]
        {
            declared(slice("ConfirmAppointment", typeof(ConfirmAppointmentEndpoint))),
            derived(slice("ConfirmAppointmentRequest", typeof(ConfirmAppointmentEndpoint)))
        });

        aligned[0].Slices.Single().Name.ShouldBe("ConfirmAppointment");
        aligned[1].Slices.Single().Name.ShouldBe("ConfirmAppointment");

        // and now -- and only now -- the merge folds them into one slice
        EventModelDescriptor.Merge("app", aligned).Slices.Single().Name.ShouldBe("ConfirmAppointment");
    }

    [Fact]
    public void registration_order_does_not_decide_which_name_survives()
    {
        var aligned = EventModelSliceAlignment.AlignSliceNames(new[]
        {
            derived(slice("ConfirmAppointmentRequest", typeof(ConfirmAppointmentEndpoint))),
            declared(slice("ConfirmAppointment", typeof(ConfirmAppointmentEndpoint)))
        });

        aligned.SelectMany(x => x.Slices).Select(x => x.Name).Distinct().ShouldBe(new[] { "ConfirmAppointment" });
    }

    [Fact]
    public void slices_that_already_agree_are_left_exactly_as_they_were()
    {
        var descriptors = new[]
        {
            declared(slice("ConfirmAppointment", typeof(ConfirmAppointmentEndpoint))),
            derived(slice("ConfirmAppointment", typeof(ConfirmAppointmentEndpoint)))
        };

        EventModelSliceAlignment.AlignSliceNames(descriptors).ShouldBeSameAs(descriptors);
    }

    [Fact]
    public void two_sources_on_the_same_rung_are_not_aligned_against_each_other()
    {
        // Wolverine core and Wolverine.HTTP are both Derived and already agree on how they name things.
        // A handler type they happen to share is not evidence that they describe one slice, and folding
        // them would silently collapse a message handler and an HTTP endpoint into a single slice.
        var descriptors = new[]
        {
            derived(slice("ConfirmAppointmentRequest", typeof(ConfirmAppointmentEndpoint))),
            derived(slice("POST /appointments", typeof(ConfirmAppointmentEndpoint)))
        };

        EventModelSliceAlignment.AlignSliceNames(descriptors).ShouldBeSameAs(descriptors);
    }

    [Fact]
    public void a_handler_type_carrying_several_slices_in_one_source_identifies_none_of_them()
    {
        // The ordinary shape of a handler class that handles more than one message: the handler type no
        // longer picks out a slice, so guessing one would be worse than leaving it unjoined.
        var aligned = EventModelSliceAlignment.AlignSliceNames(new[]
        {
            declared(slice("ConfirmAppointment", typeof(ConfirmAppointmentEndpoint))),
            derived(
                slice("ConfirmAppointmentRequest", typeof(ConfirmAppointmentEndpoint)),
                slice("CancelAppointmentRequest", typeof(ConfirmAppointmentEndpoint)))
        });

        aligned[1].Slices.Select(x => x.Name)
            .ShouldBe(new[] { "ConfirmAppointmentRequest", "CancelAppointmentRequest" });
    }

    [Fact]
    public void a_rename_that_would_collide_with_an_existing_slice_is_dropped()
    {
        var aligned = EventModelSliceAlignment.AlignSliceNames(new[]
        {
            declared(slice("ConfirmAppointment", typeof(ConfirmAppointmentEndpoint))),
            derived(
                slice("ConfirmAppointmentRequest", typeof(ConfirmAppointmentEndpoint)),
                slice("ConfirmAppointment", typeof(CancelAppointmentEndpoint)))
        });

        aligned[1].Slices.Select(x => x.Name)
            .ShouldBe(new[] { "ConfirmAppointmentRequest", "ConfirmAppointment" });
    }

    [Fact]
    public void a_slice_with_no_handler_type_has_nothing_to_join_on()
    {
        var descriptors = new[]
        {
            declared(slice("ConfirmAppointment")),
            derived(slice("ConfirmAppointmentRequest"))
        };

        EventModelSliceAlignment.AlignSliceNames(descriptors).ShouldBeSameAs(descriptors);
    }
}

// The whole point of GH-4385 in one fixture: a curated model registered beside the application it
// describes assembles as ONE model, on the board's names, carrying the code's roles -- and raising the
// real disagreement instead of duplicating every slice.
public class declared_and_derived_models_assemble_as_one_4385 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddEventModelSource(new CuratedBoard()))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "appointments";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ConfirmAppointmentHandler))
                    .IncludeType(typeof(ProposeHomeCheckAppointmentHandler));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_board_and_the_code_fold_into_one_model()
    {
        var model = await WolverineEventModelExport.AssembleAsync(_host.Services,
            token: TestContext.Current.CancellationToken);

        // two slices, not four: the board's names, joined to the code on handler type
        model.Slices.Select(x => x.Name).OrderBy(x => x)
            .ShouldBe(new[] { "ConfirmAppointment", "ProposeHomeCheckAppointment" });

        var confirm = model.Slices.Single(x => x.Name == "ConfirmAppointment");
        confirm.CommandType!.Name.ShouldBe(nameof(ConfirmAppointmentRequest));
        confirm.HandlerType!.Name.ShouldBe(nameof(ConfirmAppointmentHandler));
        confirm.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        confirm.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(AppointmentConfirmed) });

        // GH-4387: the code never claimed Pattern, so the board's Automation survives on the slice it
        // declared it for -- which is the whole reason that role was left unclaimed
        model.Slices.Single(x => x.Name == "ProposeHomeCheckAppointment").Pattern
            .ShouldBe(SlicePattern.Automation);

        // the board named the slice and nothing else claims naming, so the name is Declared while the
        // facts around it came off the chain
        confirm.ProvenanceFor(EventModelRole.CommandType).ShouldBe(EventModelProvenance.Derived);
        confirm.ProvenanceFor(EventModelRole.Domain).ShouldBe(EventModelProvenance.Declared);
    }

    [Fact]
    public async Task a_real_disagreement_now_surfaces_as_a_hotspot()
    {
        var model = await WolverineEventModelExport.AssembleAsync(_host.Services,
            token: TestContext.Current.CancellationToken);

        // the board says this slice also emits AppointmentRescheduled; the code says it does not. Before
        // the sources could meet, the two lived in different halves of one file and the drift was
        // invisible -- twenty-two slices and zero hotspots.
        var confirm = model.Slices.Single(x => x.Name == "ConfirmAppointment");
        confirm.Hotspots.ShouldContain(x =>
            x.Origin == HotspotOrigin.SourceDisagreement && x.Text.Contains(nameof(EventModelRole.EmittedEvents)));
    }
}

#region declared board and the code it describes

public record ConfirmAppointmentRequest(Guid Id);

public record AppointmentConfirmed(Guid Id);

public record AppointmentRescheduled(Guid Id);

public record AppointmentConfirmationSent(Guid Id);

public record HomeCheckAssignmentAccepted(Guid Id);

public record HomeCheckAppointmentProposed(Guid Id);

public static class ConfirmAppointmentHandler
{
    [Emits(typeof(AppointmentConfirmed))]
    public static AppointmentConfirmationSent Handle(ConfirmAppointmentRequest request)
        => new(request.Id);
}

public static class ProposeHomeCheckAppointmentHandler
{
    [Emits(typeof(HomeCheckAppointmentProposed))]
    public static void Handle(HomeCheckAssignmentAccepted trigger)
    {
    }
}

// stand-ins for the two endpoint types the alignment unit tests join on
public static class ConfirmAppointmentEndpoint;

public static class CancelAppointmentEndpoint;

/// <summary>
///     What a curated <c>.emodel.yaml</c> registers: the board's slice names, its handler role, and the
///     events it says each slice emits -- all on the Declared rung.
/// </summary>
public class CuratedBoard : IEventModelDefinitionSource
{
    public Uri Subject { get; } = new("event-model://board/appointments");

    public EventModelProvenance Provenance => EventModelProvenance.Declared;

    public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
    {
        var slices = new[]
        {
            EventModelSliceDescriptor.Named("ConfirmAppointment") with
            {
                Domain = "Appointments",
                HandlerType = TypeDescriptor.For(typeof(ConfirmAppointmentHandler)),
                EmittedEvents = new[]
                {
                    TypeDescriptor.For(typeof(AppointmentConfirmed)),
                    TypeDescriptor.For(typeof(AppointmentRescheduled))
                }
            },
            EventModelSliceDescriptor.Named("ProposeHomeCheckAppointment") with
            {
                Domain = "Appointments",
                Pattern = SlicePattern.Automation,
                HandlerType = TypeDescriptor.For(typeof(ProposeHomeCheckAppointmentHandler))
            }
        };

        return Task.FromResult<EventModelDescriptor?>(new EventModelDescriptor("CritterCrush", slices));
    }
}

#endregion
