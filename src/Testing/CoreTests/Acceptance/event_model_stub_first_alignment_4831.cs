using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel4831;

// GH-4831: a model declared stub-first has no handler type yet, so the GH-4385 join on HandlerType
// never fired -- Wolverine named an automation for its triggering event while the board named it for
// the behaviour, and the assembled model had two slices and zero hotspots. The roles a stub pins down
// join too.
public class event_model_stub_first_alignment_4831
{
    private static EventModelDescriptor declared(Action<EventModelBuilder> configure)
    {
        var builder = new EventModelBuilder();
        configure(builder);
        return builder.Build("board").WithProvenance(EventModelProvenance.Declared);
    }

    private static EventModelDescriptor derived(params EventModelSliceDescriptor[] slices)
        => new EventModelDescriptor("app", slices).WithProvenance(EventModelProvenance.Derived);

    private static EventModelSliceDescriptor handled<TMessage>(string? name = null, string? domain = null)
        => EventModelSliceDescriptor.Named(name ?? typeof(TMessage).Name) with
        {
            CommandType = TypeDescriptor.For(typeof(TMessage)),
            TriggerKind = TriggerKind.MessageHandler,
            Domain = domain
        };

    private static string[] namesAfterMerge(params EventModelDescriptor[] descriptors)
        => EventModelDescriptor.Merge("app", EventModelSliceAlignment.AlignSliceNames(descriptors))
            .Slices.Select(x => x.Name).ToArray();

    [Fact]
    public void a_declared_command_joins_the_slice_derived_for_that_command_type()
    {
        // ...which is also how an HTTP endpoint joins: Wolverine.HTTP names its slice for the request type
        namesAfterMerge(
                declared(m => m.Slice("ConfirmAppointment").Command<ConfirmAppointmentRequest>()),
                derived(handled<ConfirmAppointmentRequest>()))
            .ShouldBe(new[] { "ConfirmAppointment" });
    }

    [Fact]
    public void a_command_declared_by_name_joins_the_type_of_that_name()
    {
        namesAfterMerge(
                declared(m => m.Slice("ConfirmAppointment").Command(nameof(ConfirmAppointmentRequest))),
                derived(handled<ConfirmAppointmentRequest>()))
            .ShouldBe(new[] { "ConfirmAppointment" });
    }

    [Fact]
    public void a_declared_automation_joins_the_slice_derived_for_the_event_it_reacts_to()
    {
        // The issue's case: Wolverine names the automation for its triggering event
        namesAfterMerge(
                declared(m => m.Automation("ProposeHomeCheckAppointment").On<HomeCheckAssignmentAccepted>()),
                derived(handled<HomeCheckAssignmentAccepted>()))
            .ShouldBe(new[] { "ProposeHomeCheckAppointment" });
    }

    [Fact]
    public void a_scheduled_automation_joins_on_its_command()
    {
        namesAfterMerge(
                declared(m => m.Slice("NightlyReminders").Pattern(SlicePattern.Automation)
                    .TriggeredBy(TriggerKind.JobScheduler).Command<SendReminders>()),
                derived(handled<SendReminders>() with { TriggerKind = TriggerKind.JobScheduler }))
            .ShouldBe(new[] { "NightlyReminders" });
    }

    [Fact]
    public void a_declared_view_joins_the_slice_reading_its_read_model()
    {
        var route = EventModelSliceDescriptor.Named("GET /api/appointments/queue") with
        {
            Pattern = SlicePattern.View,
            TriggerKind = TriggerKind.Http,
            ReadModelTypes = new[] { TypeDescriptor.For(typeof(AppointmentsQueue)) }
        };

        namesAfterMerge(
                declared(m => m.Slice("Appointments Queue").Pattern(SlicePattern.View).Produces<AppointmentsQueue>()),
                derived(route))
            .ShouldBe(new[] { "Appointments Queue" });
    }

    [Fact]
    public void one_modules_declaration_joins_only_that_modules_slice()
    {
        // GH-4829's modular monolith: two derived slices for one message, told apart by domain
        var names = namesAfterMerge(
            declared(m => m.Automation("BillOrder").InDomain("Billing").On<OrderPlaced>()),
            derived(
                handled<OrderPlaced>("OrderPlaced (Billing)", "Billing"),
                handled<OrderPlaced>("OrderPlaced (Shipping)", "Shipping")));

        names.ShouldBe(new[] { "BillOrder", "OrderPlaced (Shipping)" }, ignoreOrder: true);
    }

    [Fact]
    public void a_message_several_slices_handle_identifies_none_of_them_without_a_domain()
    {
        var descriptors = new[]
        {
            declared(m => m.Automation("BillOrder").On<OrderPlaced>()),
            derived(
                handled<OrderPlaced>("OrderPlaced (Billing)", "Billing"),
                handled<OrderPlaced>("OrderPlaced (Shipping)", "Shipping"))
        };

        EventModelSliceAlignment.AlignSliceNames(descriptors).ShouldBeSameAs(descriptors);
    }

    [Fact]
    public void a_stub_join_still_refuses_to_collide_with_an_existing_slice()
    {
        var aligned = EventModelSliceAlignment.AlignSliceNames(new[]
        {
            declared(m => m.Slice("ConfirmAppointment").Command<ConfirmAppointmentRequest>()),
            derived(handled<ConfirmAppointmentRequest>(), handled<SendReminders>("ConfirmAppointment"))
        });

        aligned[1].Slices.Select(x => x.Name)
            .ShouldBe(new[] { nameof(ConfirmAppointmentRequest), "ConfirmAppointment" });
    }

    [Fact]
    public void a_slice_joined_on_its_handler_is_not_joined_again_on_its_command()
    {
        var byHandler = EventModelSliceDescriptor.Named("Confirm") with
        {
            HandlerType = TypeDescriptor.For(typeof(ConfirmAppointmentHandler))
        };

        var names = namesAfterMerge(
            declared(m =>
            {
                m.Slice("Confirm").HandledBy<ConfirmAppointmentHandler>();
                m.Slice("SomethingElse").Command<ConfirmAppointmentRequest>();
            }),
            derived(handled<ConfirmAppointmentRequest>() with
            {
                HandlerType = TypeDescriptor.For(typeof(ConfirmAppointmentHandler))
            }));

        names.ShouldBe(new[] { "Confirm", "SomethingElse" }, ignoreOrder: true);
    }

    [Fact]
    public async Task the_export_joins_a_stub_first_declaration_onto_the_handler_written_later()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "HomeChecks";
                opts.Discovery.DisableConventionalDiscovery().IncludeType<HomeCheckAssignmentAcceptedHandler>();
                opts.Services.AddEventModel("HomeChecks", m =>
                    m.Automation("ProposeHomeCheckAppointment").On<HomeCheckAssignmentAccepted>());
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var model = (await WolverineEventModelExport.AssembleSetAsync(host.Services, token: TestContext.Current.CancellationToken)).Models.Single();

        var slice = model.Slices.Single();
        slice.Name.ShouldBe("ProposeHomeCheckAppointment");
        slice.HandlerType!.Name.ShouldBe(nameof(HomeCheckAssignmentAcceptedHandler));
        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(ProposeAppointment) });
    }
}

public record ConfirmAppointmentRequest(Guid AppointmentId);

public class ConfirmAppointmentHandler;

public record HomeCheckAssignmentAccepted(Guid HomeCheckId);

public record ProposeAppointment(Guid HomeCheckId);

public class HomeCheckAssignmentAcceptedHandler
{
    public ProposeAppointment Handle(HomeCheckAssignmentAccepted e) => new(e.HomeCheckId);
}

public record SendReminders;

public record AppointmentsQueue(Guid Id);

public record OrderPlaced(Guid OrderId);
