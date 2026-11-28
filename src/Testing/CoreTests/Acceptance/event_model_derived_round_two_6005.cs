using JasperFx;
using JasperFx.Events.EventModeling;
using JasperFx.Events.Tags;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Acceptance.EventModel6005;

// GH-6005 (jasperfx#1297, #1298, #1299): the Derived rung fills the round-two roles from facts the runtime
// already has -- the store's tag registry, handler signatures, endpoint and schedule registrations, XML
// documentation comments and the record types -- and never from a method body. RejectionReasons stays
// unclaimed on this rung.
public class event_model_derived_round_two_6005
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    private static EventModelDerivation registry(params ITagTypeRegistration[] tags) => new() { TagTypes = tags };

    [Fact]
    public void dcb_reads_and_event_tags_come_from_the_registry_and_the_signatures()
    {
        var derivation = registry(TagTypeRegistration.Create<CourseId>(), TagTypeRegistration.Create<StudentId>());
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ChangeCourseCapacityHandler>(x => ChangeCourseCapacityHandler.Handle(null!, null!)), derivation);

        // The command's CourseId member is the tag the Load() query is built from; which tag the body
        // queries by is never read
        var read = slice.Reads.ShouldHaveSingleItem();
        read.Kind.ShouldBe(BoundaryReadKind.Model);
        read.Type.Name.ShouldBe(nameof(CourseState));
        read.Tag.ShouldBe("courseId");
        read.Member.ShouldBe(nameof(ChangeCourseCapacity.CourseId));

        // The emitted event carries the tag the store would infer for it: one member of a registered tag type
        slice.TagsFor(slice.EmittedEvents.Single()).ShouldBe(["courseId"]);

        // The boundary is computed from the reads and the tags
        var term = slice.BoundaryQuery.Terms.ShouldHaveSingleItem();
        term.Tag.ShouldBe("courseId");
        term.Member.ShouldBe(nameof(ChangeCourseCapacity.CourseId));
        term.EventTypes.Select(x => x.Name).ShouldBe([nameof(CourseCapacityChanged)]);

        // The handler throws for a negative capacity; the derived rung does not read that
        slice.RejectionReasons.ShouldBeEmpty();
        slice.Claims(EventModelRole.RejectionReasons).ShouldBeFalse();
    }

    [Fact]
    public void a_registry_tag_bound_to_the_model_that_the_command_does_not_carry_is_read_without_a_member()
    {
        var derivation = registry(TagTypeRegistration.Create<CourseId>(),
            TagTypeRegistration.Create<StudentId>().ForAggregate<CourseState>());
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ChangeCourseCapacityHandler>(x => ChangeCourseCapacityHandler.Handle(null!, null!)), derivation);

        slice.Reads.Select(x => (x.Tag, x.Member)).ShouldBe([("courseId", nameof(ChangeCourseCapacity.CourseId)), ("studentId", null)]);
    }

    [Fact]
    public void a_dcb_slice_described_without_a_registry_reads_by_its_strong_typed_ids()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ChangeCourseCapacityHandler>(x => ChangeCourseCapacityHandler.Handle(null!, null!)));

        slice.Reads.Select(x => (x.Tag, x.Member)).ShouldBe([("courseId", nameof(ChangeCourseCapacity.CourseId))]);
        slice.TagsFor(slice.EmittedEvents.Single()).ShouldBe(["courseId"]);
    }

    [Fact]
    public void a_slice_that_loads_no_boundary_claims_no_tags_or_reads_without_a_registry()
    {
        var slice = EventModelRoles.ForHandlerChain(chainFor<RenameCourseHandler>(x => RenameCourseHandler.Handle(null!, null!)));

        slice.Reads.ShouldBeEmpty();
        slice.EventTags.ShouldBeEmpty();
        slice.Claims(EventModelRole.EventTags).ShouldBeFalse();
    }

    [Fact]
    public void descriptions_come_from_the_xml_documentation_comments()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ChangeCourseCapacityHandler>(x => ChangeCourseCapacityHandler.Handle(null!, null!)));

        slice.Description.ShouldBe("Changes how many students a course can take.");
        slice.DescriptionOf(slice.CommandType!).ShouldBe("Asks for a new capacity on the course.");
        slice.DescriptionOf(slice.EmittedEvents.Single()).ShouldBe("The course can now take Capacity students.");

        // Nothing documented, nothing claimed
        var rename = EventModelRoles.ForHandlerChain(chainFor<RenameCourseHandler>(x => RenameCourseHandler.Handle(null!, null!)));
        rename.Description.ShouldBeNull();
        rename.ElementDescriptions.ShouldBeEmpty();

        var silent = EventModelRoles.ForHandlerChain(
            chainFor<ChangeCourseCapacityHandler>(x => ChangeCourseCapacityHandler.Handle(null!, null!)),
            new EventModelDerivation { ReadDocComments = false });
        silent.Description.ShouldBeNull();
    }

    [Fact]
    public void a_scheduled_message_is_published_and_remembered_as_scheduled()
    {
        var chain = chainFor<StartTrialHandler>(x => StartTrialHandler.Handle(null!));
        var slice = EventModelRoles.ForHandlerChain(chain);

        slice.PublishedMessages.Select(x => x.Name).ShouldBe([nameof(TrialEnded)]);
        EventModelRoles.ScheduledMessagesOf(chain, new EventModelSliceSeed(nameof(StartTrial), TriggerKind.MessageHandler, null, typeof(StartTrial), typeof(StartTrialHandler)))
            .ShouldBe([typeof(TrialEnded)]);

        var timeout = chainFor<RemindLaterHandler>(x => RemindLaterHandler.Handle(null!));
        EventModelRoles.ForHandlerChain(timeout).PublishedMessages.Select(x => x.Name).ShouldBe([nameof(ReminderDue)]);
    }

    [Fact]
    public void the_consumer_of_a_scheduled_message_is_scheduled_by_its_producer()
    {
        var start = EventModelRoles.ForHandlerChain(chainFor<StartTrialHandler>(x => StartTrialHandler.Handle(null!)));
        var end = EventModelRoles.ForHandlerChain(chainFor<TrialEndedHandler>(x => TrialEndedHandler.Handle(null!)));
        var model = new EventModelDescriptor("app", [start, end]);

        var applied = WolverineEventModelSource.ApplyScheduledMessages(model,
            new Dictionary<string, List<string>> { [typeof(TrialEnded).FullName!] = [nameof(StartTrial)] });

        applied.Slices.Single(x => x.Name == nameof(TrialEnded)).Schedule.ShouldBe(ScheduleDescriptor.Named("scheduled by StartTrial"));
        applied.Slices.Single(x => x.Name == nameof(StartTrial)).Schedule.ShouldBeNull();
    }

    [Fact]
    public void reflected_shapes_follow_the_generators_rules()
    {
        var shape = EventModelShapes.Reflect(typeof(OrderPlaced), registry(TagTypeRegistration.Create<CourseId>()));

        shape.Provenance.ShouldBe(EventModelProvenance.Derived);
        shape.Fields.Select(x => x.Name).ShouldBe(["Id", "Total", "Note", "PlacedAt", "Email", "Lines", "Discount", "CourseId", "Sku"]);

        shape.Find("Id")!.Identity.ShouldBeTrue();
        shape.Find("Total")!.Optional.ShouldBeFalse();
        shape.Find("Note")!.Optional.ShouldBeTrue();
        shape.Find("PlacedAt")!.Generated.ShouldBeTrue();
        shape.Find("Email")!.Pii.ShouldBeTrue();
        shape.Find("Lines")!.Cardinality.ShouldBe(FieldCardinality.Many);
        shape.Find("Lines")!.Type.Name.ShouldBe(nameof(OrderLine));
        shape.Find("Discount")!.Optional.ShouldBeTrue();
        shape.Find("Discount")!.Type.Name.ShouldBe(nameof(Int32));
        shape.Find("CourseId")!.Tag.ShouldBe("courseId");
        shape.Find("Sku")!.Optional.ShouldBeFalse("required takes a nullable annotation back");
        shape.Find("Email")!.Description.ShouldBe("Where the receipt goes.");
    }

    [Fact]
    public void framework_and_primitive_types_are_not_shaped()
    {
        EventModelShapes.IsShapeable(typeof(string)).ShouldBeFalse();
        EventModelShapes.IsShapeable(typeof(Guid)).ShouldBeFalse();
        EventModelShapes.IsShapeable(typeof(List<OrderLine>)).ShouldBeFalse();
        EventModelShapes.IsShapeable(typeof(OrderLine)).ShouldBeTrue();
    }

    [Fact]
    public void a_derived_actor_takes_the_name_a_declaration_gave_the_same_slice()
    {
        var declared = new EventModelBuilder();
        declared.Command<ChangeCourseCapacity>().TriggeredBy(TriggerKind.Http).Actor("Registrar", "registrar");
        var declaration = declared.Build("app").WithProvenance(EventModelProvenance.Declared);

        var derivedBuilder = new EventModelBuilder();
        derivedBuilder.Command<ChangeCourseCapacity>().TriggeredBy(TriggerKind.Http).Actor("registrar", "registrar");
        var derived = derivedBuilder.Build("app").WithProvenance(EventModelProvenance.Derived);

        var aligned = EventModelSliceAlignment.AlignActorNames([declaration, derived]);
        aligned[1].Slices.Single().Actor.ShouldBe(new ActorDescriptor("Registrar", ["registrar"]));

        // With the name aligned, agreeing roles merge without a finding...
        var merged = EventModelDescriptor.Merge("app", aligned).Slices.Single();
        merged.Actor.ShouldBe(new ActorDescriptor("Registrar", ["registrar"]));
        merged.Hotspots.ShouldNotContain(x => x.Role == EventModelRole.Actor);

        // ...and differing roles are the finding the role exists for
        var stricter = new EventModelBuilder();
        stricter.Command<ChangeCourseCapacity>().TriggeredBy(TriggerKind.Http).Actor("admin", "admin");
        var disagreeing = EventModelSliceAlignment.AlignActorNames([declaration, stricter.Build("app").WithProvenance(EventModelProvenance.Derived)]);
        EventModelDescriptor.Merge("app", disagreeing).Slices.Single().Hotspots.ShouldContain(x => x.Role == EventModelRole.Actor);
    }
}

// The model-level derivations need a whole handler graph: the tag catalogue, the reflected shapes, the
// recurring schedules and the external events
public class event_model_derived_round_two_on_a_host_6005 : IAsyncLifetime
{
    private IHost _host = null!;
    private int _paymentsPort;
    private int _auditPort;

    public async ValueTask InitializeAsync()
    {
        _paymentsPort = PortFinder.GetAvailablePort();
        _auditPort = PortFinder.GetAvailablePort();

        // Started, as external_system_3989's host is: a listener's .ExternalSystem(), .Named() and
        // DefaultIncomingMessage<T>() are endpoint configuration that is applied when the endpoint compiles
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "round-two-6005";
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RenameCourseHandler))
                    .IncludeType(typeof(StartTrialHandler))
                    .IncludeType(typeof(TrialEndedHandler))
                    .IncludeType(typeof(RemindLaterHandler))
                    .IncludeType(typeof(ReminderDueHandler))
                    .IncludeType(typeof(NightlyTickHandler))
                    .IncludeType(typeof(PaymentReceivedHandler))
                    .IncludeType(typeof(AuditEntryHandler));

                opts.Schedules.ScheduleRecurring<NightlyTick>("0 2 * * *");

                // An event from outside: nothing in this model emits PaymentReceived, and it arrives on a
                // listener named for the system that sends it
                ((ListenerConfiguration)opts.ListenAtPort(_paymentsPort))
                    .DefaultIncomingMessage<PaymentReceived>()
                    .ExternalSystem("Payments");

                // ...and one on a listener nobody named, which stands in for the system
                ((ListenerConfiguration)opts.ListenAtPort(_auditPort))
                    .DefaultIncomingMessage<AuditEntry>()
                    .Named("audit-feed");
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private EventModelDescriptor describe(EventModelDerivation? derivation = null)
        => WolverineEventModelSource.Describe(_host.Services.GetRequiredService<WolverineOptions>(), null,
            Array.Empty<DomainAssignmentDescriptor>(), derivation ?? EventModelDerivation.Default);

    [Fact]
    public void the_tag_catalogue_is_the_registry()
    {
        var model = describe(new EventModelDerivation
        {
            TagTypes = [TagTypeRegistration.Create<CourseId>(), TagTypeRegistration.Create<StudentId>()]
        });

        model.Tags.Select(x => x.Name).ShouldBe(["courseId", "studentId"]);
        model.Tags[0].Type!.Name.ShouldBe(nameof(CourseId));
        model.Tags[0].InnerType!.Name.ShouldBe(nameof(Guid));

        describe().Tags.ShouldBeEmpty();
    }

    [Fact]
    public void the_shapes_of_every_type_the_slices_name_are_reflected_unless_told_not_to()
    {
        var model = describe();

        var shape = model.ShapeOf(model.Slices.Single(x => x.Name == nameof(RenameCourse)).CommandType!).ShouldNotBeNull();
        shape.Fields.Select(x => x.Name).ShouldBe([nameof(RenameCourse.CourseId), nameof(RenameCourse.Name)]);
        shape.Provenance.ShouldBe(EventModelProvenance.Derived);
        model.ShapeOf(JasperFx.Descriptors.TypeDescriptor.For(typeof(CourseRenamed))).ShouldNotBeNull();

        describe(new EventModelDerivation { ReflectShapes = false }).Shapes.ShouldBeEmpty();
    }

    [Fact]
    public void a_recurring_registration_is_the_slices_cron_schedule()
    {
        var slice = describe().Slices.Single(x => x.Name == nameof(NightlyTick));

        slice.TriggerKind.ShouldBe(TriggerKind.JobScheduler);
        slice.Schedule.ShouldBe(ScheduleDescriptor.OnCron("0 2 * * *"));
    }

    [Fact]
    public void scheduled_message_consumers_are_scheduled_by_their_producers()
    {
        var model = describe();

        model.Slices.Single(x => x.Name == nameof(TrialEnded)).Schedule.ShouldBe(ScheduleDescriptor.Named("scheduled by StartTrial"));
        model.Slices.Single(x => x.Name == nameof(ReminderDue)).Schedule.ShouldBe(ScheduleDescriptor.Named("scheduled by RemindLater"));
        model.Slices.Single(x => x.Name == nameof(StartTrial)).Schedule.ShouldBeNull();
    }

    [Fact]
    public void a_message_arriving_from_outside_that_no_slice_emits_is_an_external_event()
    {
        var model = describe();

        model.Slices.Select(x => x.Name).ShouldContain(nameof(PaymentReceived));
        var payment = model.Slices.Single(x => x.Name == nameof(PaymentReceived));
        payment.ExternalSystemFor(payment.CommandType!).ShouldBe("Payments");

        // the listener's name stands in for a system nobody named
        var audit = model.Slices.Single(x => x.Name == nameof(AuditEntry));
        audit.ExternalSystemFor(audit.CommandType!).ShouldBe("audit-feed");

        // a message a slice in the model produces is not external, and a local one arrives from nowhere outside
        model.Slices.Single(x => x.Name == nameof(TrialEnded)).ExternalEvents.ShouldBeEmpty();
        model.Slices.Single(x => x.Name == nameof(RenameCourse)).ExternalEvents.ShouldBeEmpty();
    }

    [Fact]
    public void describe_event_model_prints_the_new_facts()
    {
        var text = EventModelReport.Plain(describe()).ReplaceLineEndings("\n");

        text.ShouldContain("schedule: cron 0 2 * * *");
        text.ShouldContain("schedule: scheduled by StartTrial");
        text.ShouldContain("external event: PaymentReceived from Payments");
        text.ShouldContain($"fields of {nameof(RenameCourse)}: CourseId: CourseId, Name: String");
    }
}

// GH-6005: what describe-event-model prints for a declared slice carrying the round-two roles
public class describe_event_model_round_two_6005
{
    [Fact]
    public void the_boundary_table_refusals_actor_schedule_and_fields_are_printed_per_slice()
    {
        var builder = new EventModelBuilder();
        builder.InChapter("Courses");
        builder.Tag<CourseId>();
        builder.Command<ChangeCourseCapacity>().TriggeredBy(TriggerKind.Http)
            .Actor("Registrar", "registrar")
            .Decides<CourseState>().ByTag<ChangeCourseCapacity, CourseId>(x => x.CourseId)
            .Emits<CourseCapacityChanged>().TaggedBy<CourseId>()
            .Refuses("Capacity cannot be negative")
            .Refuses("Course is full");
        builder.Automation<NightlyTick>().Every(TimeSpan.FromMinutes(5));
        builder.Shape<ChangeCourseCapacity>().Field("Capacity").OfType<int>().Field("CourseId").OfType<CourseId>().Identity();

        var text = EventModelReport.Plain(builder.Build("Courses")).ReplaceLineEndings("\n");

        text.ShouldContain("actor: Registrar [registrar]");
        text.ShouldContain("tags: CourseCapacityChanged [courseId]");
        text.ShouldContain("boundary reads: CourseState (model) by courseId from CourseId");
        text.ShouldContain("boundary query: courseId: CourseCapacityChanged");
        text.ShouldContain("refuses: \"Capacity cannot be negative\"; \"Course is full\"");
        text.ShouldContain("schedule: every 00:05:00");
        text.ShouldContain("fields of ChangeCourseCapacity: Capacity: Int32, CourseId: CourseId (identity)");
    }
}

public record CourseId(Guid Value);
public record StudentId(Guid Value);

/// <summary>Asks for a new capacity on the course.</summary>
public record ChangeCourseCapacity(CourseId CourseId, int Capacity);

/// <summary>The course can now take <see cref="Capacity"/> students.</summary>
public record CourseCapacityChanged(CourseId CourseId, int Capacity);

public class CourseState
{
    public int Capacity { get; set; }
}

/// <summary>Changes how many students a course can take.</summary>
public class ChangeCourseCapacityHandler
{
    public static EventTagQuery Load(ChangeCourseCapacity command) => EventTagQuery.For(command.CourseId);

    public static CourseCapacityChanged? Handle(ChangeCourseCapacity command, [DcbModel] CourseState state)
    {
        if (command.Capacity < 0) throw new InvalidOperationException("Capacity cannot be negative");
        return command.Capacity == state.Capacity ? null : new CourseCapacityChanged(command.CourseId, command.Capacity);
    }
}

public record RenameCourse(CourseId CourseId, string Name);
public record CourseRenamed(CourseId CourseId, string Name);

public class RenameCourseHandler
{
    public static CourseRenamed Handle(RenameCourse command, [WriteModel] CourseState state) => new(command.CourseId, command.Name);
}

public record StartTrial(Guid CustomerId);
public record TrialEnded(Guid CustomerId);

public class StartTrialHandler
{
    public static ScheduledMessage<TrialEnded> Handle(StartTrial command)
        => new(new TrialEnded(command.CustomerId), DateTimeOffset.UtcNow.AddDays(14));
}

public class TrialEndedHandler
{
    public static void Handle(TrialEnded message) { }
}

public record RemindLater(Guid CustomerId);
public record ReminderDue(Guid CustomerId) : TimeoutMessage(TimeSpan.FromHours(1));

public class RemindLaterHandler
{
    public static ReminderDue Handle(RemindLater command) => new(command.CustomerId);
}

public class ReminderDueHandler
{
    public static void Handle(ReminderDue message) { }
}

public record NightlyTick;

public class NightlyTickHandler
{
    public static void Handle(NightlyTick message) { }
}

public record PaymentReceived(Guid OrderId, decimal Amount);

public class PaymentReceivedHandler
{
    public static void Handle(PaymentReceived message) { }
}

public record AuditEntry(string Text);

public class AuditEntryHandler
{
    public static void Handle(AuditEntry message) { }
}

public record OrderLine(string Sku, int Quantity);

/// <summary>An order was placed.</summary>
/// <param name="Email">Where the receipt goes.</param>
public record OrderPlaced(
    Guid Id,
    decimal Total,
    string? Note,
    [JasperFx.Generated] DateTimeOffset PlacedAt,
    [Pii] string Email,
    List<OrderLine> Lines,
    int? Discount,
    CourseId CourseId)
{
    public required string? Sku { get; init; }
}
