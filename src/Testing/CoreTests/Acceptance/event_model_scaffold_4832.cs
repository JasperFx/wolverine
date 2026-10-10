using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Wolverine.Configuration.EventModeling.Scaffolding;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace CoreTests.Acceptance.EventModel4832;

// GH-4832: the scaffold command writes implementation skeletons for the slices that are declared but
// have no code behind them yet, from the declared roles and the real stub types, purely through
// ISourceWriter, and in Wolverine's store-agnostic style.
public class event_model_scaffold_4832
{
    private static EventModelDescriptor declared(Action<EventModelBuilder> configure)
    {
        var builder = new EventModelBuilder();
        configure(builder);
        return builder.Build("Clinic").WithProvenance(EventModelProvenance.Declared);
    }

    private static Type? resolve(TypeDescriptor descriptor)
        => typeof(event_model_scaffold_4832).Assembly.GetTypes()
            .FirstOrDefault(x => x.Namespace == typeof(event_model_scaffold_4832).Namespace && x.Name == descriptor.Name);

    private static ScaffoldPlan plan(EventModelDescriptor model, Func<string, bool>? exists = null,
        Func<Type, string?>? findSource = null)
        => SliceScaffolder.Plan(model, new SliceScaffoldOptions
        {
            RootNamespace = "Clinic",
            ResolveType = resolve,
            FileExists = exists ?? (_ => false),
            FindSourceFile = findSource ?? (_ => null)
        });

    [Fact]
    public void a_declared_http_command_becomes_a_store_agnostic_endpoint()
    {
        var result = plan(declared(m => m.InDomain("Scheduling")
            .Slice("ConfirmAppointment").Pattern(SlicePattern.Command).TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()));

        var file = result.Files.Single(x => x.RelativePath == Path.Combine("Scheduling", "ConfirmAppointment.cs"));

        file.Code.ShouldContain("namespace Clinic.Scheduling;");
        file.Code.ShouldContain("public static class ConfirmAppointmentEndpoint");
        file.Code.ShouldContain("public static ProblemDetails Validate(ConfirmAppointmentRequest command, Appointment appointment)");
        file.Code.ShouldContain("[WolverinePost(\"/api/confirm-appointment\")]");
        file.Code.ShouldContain("[EmptyResponse]");

        // GH-4889: one event onto the stream it loads -- the signature says so, so no [Emits]
        file.Code.ShouldContain("public static AppointmentConfirmed? Post(ConfirmAppointmentRequest command, [WriteModel] Appointment appointment)");
        file.Code.ShouldNotContain("[Emits(");
        file.Code.ShouldContain($"using {typeof(Appointment).Namespace};");

        // never a store-specific attribute
        file.Code.ShouldNotContain("WriteAggregate");
        file.Code.ShouldNotContain("Marten");
    }

    [Fact]
    public void an_existing_aggregate_gets_its_missing_apply_methods_inserted_into_its_own_file()
    {
        // GH-4898: the import writes aggregates as bare stubs; without these every spec fails to project
        var result = plan(
            declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
                .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()),
            findSource: type => $"Domain/{type.Name}.cs");

        var insert = result.Files.Single(x => x.InsertInto is not null);
        insert.RelativePath.ShouldBe("Domain/Appointment.cs");
        insert.InsertInto.ShouldBe(nameof(Appointment));
        insert.Code.ShouldContain("public void Apply(AppointmentConfirmed e)");
        insert.Code.ShouldContain("// TODO: fold AppointmentConfirmed into the aggregate");

        result.Notices.ShouldNotContain(x => x.Kind == ScaffoldNoticeKind.Edit);
        result.Notices.ShouldContain(x => x.Kind == ScaffoldNoticeKind.Wrote && x.Path == "Domain/Appointment.cs");
    }

    [Fact]
    public void an_existing_aggregate_whose_source_is_not_found_gets_a_report_of_what_to_add()
    {
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()));

        result.Files.ShouldNotContain(x => x.InsertInto != null);
        var edit = result.Notices.Single(x => x.Kind == ScaffoldNoticeKind.Edit);
        edit.Subject.ShouldBe($"{typeof(Appointment).FullName} (aggregate)");
        edit.Message.ShouldContain("public void Apply(AppointmentConfirmed e)");
    }

    private static readonly ScaffoldFile ApplyConfirmed = new("Domain/Appointment.cs",
        "public void Apply(AppointmentConfirmed e)\n{\n    // TODO: fold AppointmentConfirmed into the aggregate\n}\n")
    {
        InsertInto = "Appointment",
        Usings = ["Clinic.Scheduling"]
    };

    [Fact]
    public void apply_methods_are_inserted_at_the_end_of_the_class_body_with_their_usings()
    {
        const string existing = """
            using System;

            namespace Clinic.Domain;

            // A bare stub, as the import writes it
            public class Appointment
            {
                public Guid Id { get; set; }
            }

            public record Other(string Text);
            """;

        var updated = SliceScaffolder.InsertInto(existing, ApplyConfirmed)!.ReplaceLineEndings("\n");

        updated.ShouldContain("""
            using System;
            using Clinic.Scheduling;
            """.ReplaceLineEndings("\n"));
        updated.ShouldContain("""
            public class Appointment
            {
                public Guid Id { get; set; }

                public void Apply(AppointmentConfirmed e)
                {
                    // TODO: fold AppointmentConfirmed into the aggregate
                }
            }

            public record Other(string Text);
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void braces_in_strings_and_comments_do_not_end_the_class_early()
    {
        const string existing = """
            namespace Clinic.Domain
            {
                public class Appointment
                {
                    // a } in a comment
                    public string Text { get; set; } = "a } in a string";
                    /* and { here */
                }
            }
            """;

        var updated = SliceScaffolder.InsertInto(existing, ApplyConfirmed)!.ReplaceLineEndings("\n");

        updated.ShouldContain("""
                    /* and { here */

                    public void Apply(AppointmentConfirmed e)
            """.ReplaceLineEndings("\n"));
        updated.TrimEnd().ShouldEndWith("""
                    }
                }
            }
            """.ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void a_missing_class_changes_nothing()
    {
        SliceScaffolder.InsertInto("namespace Clinic;\npublic class Visit { }\n", ApplyConfirmed).ShouldBeNull();
    }

    [Fact]
    public void a_positional_record_with_no_body_gets_one()
    {
        var updated = SliceScaffolder.InsertInto("namespace Clinic;\n\npublic record Appointment(Guid Id, string Status);\n", ApplyConfirmed)!
            .ReplaceLineEndings("\n");

        updated.ShouldContain("""
            public record Appointment(Guid Id, string Status)
            {
                public void Apply(AppointmentConfirmed e)
            """.ReplaceLineEndings("\n"));
        updated.TrimEnd().ShouldEndWith("}");
    }

    [Fact]
    public void a_body_written_on_one_line_is_opened_up_first()
    {
        // The import writes aggregate stubs this way
        var updated = SliceScaffolder.InsertInto("namespace Clinic;\n\npublic class Appointment { public Guid Id { get; set; } }\n", ApplyConfirmed)!
            .ReplaceLineEndings("\n");

        updated.ShouldContain("""
            public class Appointment
            {
                public Guid Id { get; set; }

                public void Apply(AppointmentConfirmed e)
                {
                    // TODO: fold AppointmentConfirmed into the aggregate
                }
            }
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void a_record_builds_itself_from_its_first_event_and_folds_the_rest_as_copies()
    {
        // A record has nothing to fold its first event into, so it needs a static Create for it; its
        // members are init-only, so every later Apply returns the new state
        var result = plan(
            declared(m => m.View<AppointmentBoard>().From<AppointmentConfirmed>().From<AppointmentReminderScheduled>()),
            findSource: type => $"Domain/{type.Name}.cs");

        var insert = result.Files.Single(x => x.InsertInto == nameof(AppointmentBoard));
        insert.Code.ShouldContain("""
                                  public static AppointmentBoard Create(AppointmentConfirmed e)
                                  {
                                      // TODO: build the view from AppointmentConfirmed, the first event it sees
                                      throw new NotImplementedException("TODO: AppointmentBoard from AppointmentConfirmed");
                                  }
                                  """.ReplaceLineEndings("\n"));
        insert.Code.ShouldNotContain("Apply(AppointmentConfirmed e)");
        insert.Code.ShouldContain("public AppointmentBoard Apply(AppointmentReminderScheduled e)");
        insert.Code.ShouldContain("return this;");
        insert.Usings.ShouldContain("System");
    }

    [Fact]
    public void a_record_view_gets_a_create_for_every_event_that_starts_a_stream_and_they_come_first()
    {
        // The view folds the reminder first in its declaration, but the confirmation starts the stream
        var result = plan(
            declared(m =>
            {
                m.Slice("ZStartAppointment").TriggeredBy(TriggerKind.MessageHandler)
                    .Command<ProposeAppointment>().StartsStream<Appointment>().Emits<AppointmentConfirmed>();
                m.View<AppointmentBoard>().From<AppointmentReminderScheduled>().From<AppointmentConfirmed>();
            }),
            findSource: type => $"Domain/{type.Name}.cs");

        var code = result.Files.Single(x => x.InsertInto == nameof(AppointmentBoard)).Code;
        var create = code.IndexOf("public static AppointmentBoard Create(AppointmentConfirmed e)", StringComparison.Ordinal);
        var apply = code.IndexOf("public AppointmentBoard Apply(AppointmentReminderScheduled e)", StringComparison.Ordinal);

        create.ShouldBeGreaterThanOrEqualTo(0);
        apply.ShouldBeGreaterThan(create);
    }

    [Fact]
    public void an_aggregates_apply_methods_put_the_events_that_start_its_stream_first()
    {
        // The slices are scaffolded in name order, so the reminder would otherwise come first
        var result = plan(
            declared(m =>
            {
                m.Slice("AddReminder").TriggeredBy(TriggerKind.MessageHandler)
                    .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentReminderScheduled>();
                m.Slice("StartAppointment").TriggeredBy(TriggerKind.MessageHandler)
                    .Command<ProposeAppointment>().StartsStream<Appointment>().Emits<AppointmentConfirmed>();
            }),
            findSource: type => $"Domain/{type.Name}.cs");

        var code = result.Files.Single(x => x.InsertInto == nameof(Appointment)).Code;

        // A class folds its first event like any other, so it needs no Create
        code.ShouldNotContain("Create(");
        code.IndexOf("Apply(AppointmentConfirmed e)", StringComparison.Ordinal)
            .ShouldBeLessThan(code.IndexOf("Apply(AppointmentReminderScheduled e)", StringComparison.Ordinal));
    }

    [Fact]
    public void the_edit_report_finds_the_stubs_source_file_on_disk()
    {
        var finder = ScaffoldCommand.SourceFiles.Finder(AppContext.BaseDirectory);
        var path = finder(typeof(Appointment));

        path.ShouldNotBeNull();
        path.ShouldEndWith("event_model_scaffold_4832.cs");
    }

    [Fact]
    public void the_source_finder_stops_at_a_git_worktree_whose_dot_git_is_a_file()
    {
        // GH-4885: a worktree's .git is a file ("gitdir: ..."), not a directory
        var outer = Path.Combine(Path.GetTempPath(), "scaffold-4885-" + Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(outer, "worktree");
        var project = Path.Combine(worktree, "src", "App");
        Directory.CreateDirectory(Path.Combine(outer, ".git"));
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/worktree");

        try
        {
            ScaffoldCommand.SourceFiles.SolutionRoot(project).ShouldBe(new DirectoryInfo(worktree).FullName);
        }
        finally
        {
            Directory.Delete(outer, true);
        }
    }

    [Fact]
    public void a_command_whose_trigger_nothing_reveals_is_reported_unknown_rather_than_guessed()
    {
        var result = plan(declared(m => m.Command<ConfirmAppointmentRequest>()));

        result.Files.ShouldBeEmpty();
        var notice = result.Notices.Single();
        notice.Kind.ShouldBe(ScaffoldNoticeKind.UnknownTrigger);
        notice.Message.ShouldContain("trigger Unknown");
    }

    [Fact]
    public void an_automation_that_starts_a_stream_returns_StartStream_and_scaffolds_the_new_aggregate()
    {
        // Every type here is declared by NAME, so the scaffold writes their stubs too
        var result = plan(declared(m => m.Automation("OpenCase").InDomain("Cases")
            .On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened")));

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("Cases", "OpenCase.cs"));
        handler.Code.ShouldContain("public static class OpenCaseHandler");
        handler.Code.ShouldContain("public static StartStream Handle(PatientReferred patientReferred)");
        handler.Code.ShouldContain("Storage.StartStream<ClinicCase>(id, new CaseOpened(...));");

        // GH-4888: a sequential Guid for the new stream, never a random one
        handler.Code.ShouldContain("var id = Guid.CreateVersion7();");
        handler.Code.ShouldNotContain("NewGuid");

        // StartStream erases the event types, and the source generator reads them from the body (GH-4914)
        handler.Code.ShouldNotContain("[Emits(");
        handler.Code.ShouldContain("public record PatientReferred;");
        handler.Code.ShouldContain("public record CaseOpened;");
        handler.Code.ShouldNotContain("public record ClinicCase;");

        var aggregate = result.Files.Single(x => x.RelativePath == Path.Combine("Cases", "ClinicCase.cs"));
        aggregate.Code.ShouldContain("public class ClinicCase");
        aggregate.Code.ShouldContain("public void Apply(CaseOpened e)");
    }

    [Fact]
    public void an_automation_issues_its_command_as_an_outgoing_message()
    {
        var result = plan(declared(m => m.Automation("ProposeHomeCheckAppointment")
            .On<HomeCheckAssignmentAccepted>().Command<ProposeAppointment>()));

        var code = result.Files.Single().Code;
        code.ShouldContain("public static OutgoingMessages Handle(HomeCheckAssignmentAccepted homeCheckAssignmentAccepted)");
        code.ShouldContain("new ProposeAppointment(...)");
    }

    [Fact]
    public void a_view_declared_by_name_gets_apply_methods_for_the_events_it_folds()
    {
        var result = plan(declared(m => m.View("AppointmentsQueue").From<AppointmentConfirmed>()));

        var view = result.Files.Single().Code;
        view.ShouldContain("public class AppointmentsQueue");
        view.ShouldContain("public void Apply(AppointmentConfirmed e)");
    }

    [Fact]
    public void a_one_event_slice_returns_the_event_type_and_needs_no_emits_attribute()
    {
        // GH-4889
        var code = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>())).Files.Single().Code;

        code.ShouldContain("public static AppointmentConfirmed? Handle(ConfirmAppointmentRequest command, [WriteModel] Appointment appointment)");
        code.ShouldContain("or null when there is nothing to record");
        code.ShouldContain("If the stream may not exist yet, make the parameter Appointment?");
        code.ShouldNotContain("[Emits(");
        code.ShouldNotContain("EventsToAppend");
    }

    [Fact]
    public void a_slice_that_also_sends_messages_keeps_events_to_append_with_no_emits()
    {
        var code = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()
            .Publishes<AppointmentReminderScheduled>())).Files.Single().Code;

        code.ShouldContain("public static (EventsToAppend, OutgoingMessages) Handle(");
        code.ShouldNotContain("[Emits(");
    }

    [Fact]
    public void a_command_with_no_aggregate_is_a_todo_and_a_warning_never_an_untyped_append()
    {
        // GH-4895: only a slice that purely starts a stream may do without an aggregate, and nothing
        // says this one does -- so nothing is guessed
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>().Emits<AppointmentConfirmed>()));
        var code = result.Files.Single().Code;

        code.ShouldContain("public static void Handle(ConfirmAppointmentRequest command)");
        code.ShouldContain("TODO: the model names no aggregate this command decides against");
        code.ShouldContain(".Against<T>(), once per stream, or .StartsStream<T>()");
        code.ShouldNotContain("AppendEvents");

        var warning = result.Notices.Single(x => x.Kind == ScaffoldNoticeKind.Warning);
        warning.Subject.ShouldBe("ConfirmAppointment");
        warning.ToString().ShouldStartWith("⚠ WARN");
    }

    [Fact]
    public void a_for_aggregate_default_scaffolds_like_an_explicit_against()
    {
        // GH-4919: model.ForAggregate<T>() (jasperfx#994) gives a command that declares no aggregate its own
        var code = plan(declared(m =>
        {
            m.ForAggregate<Appointment>();
            m.Command<ConfirmAppointmentRequest>().TriggeredBy(TriggerKind.MessageHandler).Emits<AppointmentConfirmed>();
        })).Files.Single().Code;

        code.ShouldContain("public static AppointmentConfirmed? Handle(ConfirmAppointmentRequest command, [WriteModel] Appointment appointment)");
    }

    [Fact]
    public void no_aggregate_scaffolds_a_stream_with_no_aggregate_type_and_no_warning()
    {
        // GH-4919: .NoAggregate() is the declared, deliberate case GH-4895 otherwise warns about
        var result = plan(declared(m => m.Command<ConfirmAppointmentRequest>().TriggeredBy(TriggerKind.MessageHandler)
            .NoAggregate().Emits<AppointmentConfirmed>()));
        var code = result.Files.Single().Code;

        code.ShouldContain("public static StartStream Handle(ConfirmAppointmentRequest command)");
        code.ShouldContain("var id = Guid.CreateVersion7();");
        code.ShouldContain("return Storage.StartStream(id, new AppointmentConfirmed(...));");
        code.ShouldNotContain("TODO: the model names no aggregate");
        result.Notices.ShouldNotContain(x => x.Kind == ScaffoldNoticeKind.Warning);
    }

    [Fact]
    public void a_dcb_decider_model_is_a_self_aggregate_fetched_by_the_commands_strong_typed_ids()
    {
        // GH-4865: DeciderModel<T>() (jasperfx#994) scaffolds [DcbModel] T, fetched by the EventTagQuery a Load
        // method builds from the command's strong-typed ids, and T folds what the slice emits
        var result = plan(
            declared(m => m.Command<ReserveSeat>().TriggeredBy(TriggerKind.MessageHandler)
                .DeciderModel<SeatAvailability>().Emits<SeatReserved>()),
            findSource: type => $"Domain/{type.Name}.cs");
        var code = result.Files.Single(x => x.InsertInto is null).Code;

        code.ShouldContain("""
                               public static EventTagQuery Load(ReserveSeat command)
                               {
                                   // The tags this decision reads; narrow it to the events it needs with .AndEventsOfType<...>()
                                   return EventTagQuery.For(command.Screening).Or(command.Customer);
                               }
                           """.ReplaceLineEndings("\n"));
        code.ShouldContain("public static SeatReserved Handle(ReserveSeat command, [DcbModel] SeatAvailability seatAvailability)");
        code.ShouldContain("return new SeatReserved(...);   // appended through the SeatAvailability boundary");
        code.ShouldNotContain("[WriteModel]");

        // A self-aggregate: the decider folds what the slice emits
        result.Files.Single(x => x.InsertInto == nameof(SeatAvailability)).Code.ShouldContain("public void Apply(SeatReserved e)");

        var register = result.Notices.Single(x => x.Kind == ScaffoldNoticeKind.Edit && x.Subject == "ReserveSeat");
        register.Message.ShouldContain("(ScreeningTag, CustomerTag)");
        result.Notices.ShouldNotContain(x => x.Kind == ScaffoldNoticeKind.Warning);
    }

    [Fact]
    public void a_multi_stream_view_gets_a_projection_with_its_identity_rules_to_write_and_an_async_registration()
    {
        // wolverine#4865: AsMultiStream() -- the identity rule is the one thing the model cannot say
        var result = SliceScaffolder.Plan(
            declared(m => m.View<AppointmentBoard>().From<AppointmentConfirmed>().From<AppointmentReminderScheduled>().AsMultiStream()),
            new SliceScaffoldOptions
            {
                RootNamespace = "Clinic",
                ResolveType = resolve,
                FindSourceFile = type => $"Domain/{type.Name}.cs",
                MultiStreamProjectionBase = "Marten.Events.Projections.MultiStreamProjection"
            });

        var projection = result.Files.Single(x => x.RelativePath == "AppointmentBoardProjection.cs").Code;
        projection.ShouldContain("public class AppointmentBoardProjection : Marten.Events.Projections.MultiStreamProjection<AppointmentBoard, Guid>");
        projection.ShouldContain("//     Identity<AppointmentConfirmed>(e => e.AppointmentBoardId);");
        projection.ShouldContain("//     Identity<AppointmentReminderScheduled>(e => e.AppointmentBoardId);");

        result.Notices.ShouldContain(x => x.Kind == ScaffoldNoticeKind.Edit
                                          && x.Message.Contains("opts.Projections.Add<AppointmentBoardProjection>(ProjectionLifecycle.Async)"));

        // The view still folds what it consumes itself
        result.Files.Single(x => x.InsertInto == nameof(AppointmentBoard)).Code.ShouldContain("Create(AppointmentConfirmed e)");
    }

    [Fact]
    public void a_multi_stream_view_on_a_store_the_scaffold_does_not_know_is_only_described()
    {
        var result = plan(declared(m => m.View<AppointmentBoard>().From<AppointmentConfirmed>().AsMultiStream()));

        result.Files.ShouldNotContain(x => x.RelativePath.EndsWith("Projection.cs"));
        result.Notices.ShouldContain(x => x.Kind == ScaffoldNoticeKind.Edit && x.Message.Contains("ProjectionLifecycle.Async"));
    }

    [Fact]
    public void a_dcb_command_with_no_strong_typed_ids_leaves_its_tags_a_todo()
    {
        var result = plan(declared(m => m.Command<ConfirmAppointmentRequest>().TriggeredBy(TriggerKind.MessageHandler)
            .DeciderModel<Appointment>().Emits<AppointmentConfirmed>()));
        var code = result.Files.Single(x => x.InsertInto is null).Code;

        code.ShouldContain("throw new NotImplementedException(\"TODO: the tags ConfirmAppointmentRequest reads\");");
        code.ShouldContain("[DcbModel] Appointment appointment");

        var warning = result.Notices.Single(x => x.Kind == ScaffoldNoticeKind.Warning);
        warning.Message.ShouldContain("GH-4883");
    }

    [Fact]
    public void a_command_against_several_aggregates_takes_an_event_stream_per_aggregate()
    {
        // GH-4895: one IEventStream<T> per stream, each found by its own {Aggregate}Id member
        var result = plan(declared(m => m.Slice("AcceptHomeCheckAssignment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<AcceptHomeCheckAssignment>().Against<HomeCheck>().Against<VolunteerApplication>()
            .Emits<HomeCheckAssignmentAcceptedEvent>()));
        var code = result.Files.Single().Code;

        // The command is just `command`, and a signature this long puts each parameter on its own line.
        // A parameter still past 120 columns puts its attribute on the line above, and so then does every
        // other attributed parameter
        code.ShouldContain("""
                               public static void Handle(
                                       AcceptHomeCheckAssignment command,
                                       [WriteModel(nameof(AcceptHomeCheckAssignment.HomeCheckId))]
                                       IEventStream<HomeCheck> homeCheckStream,
                                       [WriteModel(nameof(AcceptHomeCheckAssignment.VolunteerApplicationId))]
                                       IEventStream<VolunteerApplication> volunteerApplicationStream)
                                   {
                               """.ReplaceLineEndings("\n"));
        code.Split('\n').ShouldAllBe(line => line.Length <= 120);
        code.ShouldContain("homeCheckStream.AppendOne(new HomeCheckAssignmentAcceptedEvent(...));   // or volunteerApplicationStream");

        // IEventStream<T> erases the event types; the source generator reads AppendOne instead (GH-4914)
        code.ShouldNotContain("[Emits(");
        result.Notices.ShouldNotContain(x => x.Kind == ScaffoldNoticeKind.Warning);
    }

    [Fact]
    public void a_stream_the_command_has_no_id_member_for_is_a_warning()
    {
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Against<HomeCheck>()
            .Emits<AppointmentConfirmed>()));

        result.Files.Single().Code.ShouldContain("[WriteModel(\"HomeCheckId\")] IEventStream<HomeCheck> homeCheckStream");
        result.Notices.Single(x => x.Kind == ScaffoldNoticeKind.Warning)
            .Message.ShouldContain("ConfirmAppointmentRequest has no HomeCheckId member");
    }

    [Fact]
    public void a_human_triggered_command_is_scaffolded_as_a_message_handler_and_the_report_says_so()
    {
        // GH-4884: every command an imported board produces is screen-triggered
        var result = plan(declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Human)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()));

        result.Files.Single(x => x.RelativePath == "ConfirmAppointment.cs").Code
            .ShouldContain("public static class ConfirmAppointmentHandler");

        var notice = result.Notices.Single(x => x.Subject == "ConfirmAppointment");
        notice.Kind.ShouldBe(ScaffoldNoticeKind.Wrote);
        notice.Message.ShouldContain("TriggerKind.Human");
        notice.Message.ShouldContain("message handler");
    }

    [Fact]
    public void slices_and_their_aggregates_go_in_a_folder_and_namespace_per_chapter()
    {
        // GH-4891
        var result = plan(declared(m => m.InChapter("Volunteering And Home Checks")
            .Automation("OpenCase").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened")));

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("VolunteeringAndHomeChecks", "OpenCase.cs"));
        handler.Code.ShouldContain("namespace Clinic.VolunteeringAndHomeChecks;");

        result.Files.ShouldContain(x => x.RelativePath == Path.Combine("VolunteeringAndHomeChecks", "ClinicCase.cs"));
    }

    [Fact]
    public void the_handler_is_appended_to_the_file_that_declares_its_command()
    {
        // GH-4891: one file per slice -- the command and its handler together
        var result = plan(
            declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
                .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()),
            findSource: type => type == typeof(ConfirmAppointmentRequest) ? "Features/ConfirmAppointment.cs" : null);

        var append = result.Files.Single(x => x.AppendClass is not null);
        append.RelativePath.ShouldBe("Features/ConfirmAppointment.cs");
        append.AppendClass.ShouldBe("ConfirmAppointmentHandler");
        append.Namespace.ShouldBe(typeof(ConfirmAppointmentRequest).Namespace);
        append.Code.ShouldNotContain("namespace ");
        append.Code.ShouldNotContain("using ");
        append.Usings.ShouldContain("Wolverine.Persistence.EventSourcing");

        result.Notices.Single(x => x.Subject == "ConfirmAppointment").Message
            .ShouldContain("appended to the file that declares ConfirmAppointmentRequest");
    }

    private static ScaffoldFile appendOf(string code) => new("Features/ApplyToVolunteer.cs", code)
    {
        AppendClass = "ApplyToVolunteerHandler",
        Usings = new[] { "System", "Wolverine", "Wolverine.Persistence", "CritterCrush.Volunteering" },
        Namespace = "CritterCrush.Volunteering"
    };

    private const string AppendedHandler = "public static class ApplyToVolunteerHandler\n{\n}";

    [Fact]
    public void appending_into_a_file_scoped_namespace_adds_the_missing_usings_after_the_files_own()
    {
        var existing = "using System;\n\nnamespace CritterCrush.Volunteering;\n\npublic record ApplyToVolunteer(Guid Id);\n";

        var result = SliceScaffolder.AppendTo(existing, appendOf(AppendedHandler))!;

        result.ShouldBe("using System;\nusing Wolverine;\nusing Wolverine.Persistence;\n\nnamespace CritterCrush.Volunteering;\n\npublic record ApplyToVolunteer(Guid Id);\n\npublic static class ApplyToVolunteerHandler\n{\n}\n");
    }

    [Fact]
    public void appending_into_a_block_scoped_namespace_wraps_the_class_in_its_own_namespace_block()
    {
        var existing = "namespace CritterCrush.Volunteering\n{\n    public record ApplyToVolunteer(System.Guid Id);\n}\n";

        var result = SliceScaffolder.AppendTo(existing, appendOf(AppendedHandler))!;

        result.ShouldStartWith("using System;\nusing Wolverine;\nusing Wolverine.Persistence;\n\nnamespace CritterCrush.Volunteering\n{");
        result.ShouldEndWith("}\n\nnamespace CritterCrush.Volunteering\n{\n    public static class ApplyToVolunteerHandler\n    {\n    }\n}\n");
    }

    [Fact]
    public void a_class_already_in_the_file_is_never_appended_twice()
    {
        var existing = "namespace CritterCrush.Volunteering;\n\npublic static class ApplyToVolunteerHandler\n{\n}\n";

        SliceScaffolder.AppendTo(existing, appendOf(AppendedHandler)).ShouldBeNull();
    }

    [Fact]
    public void the_scaffold_mints_no_random_guids_anywhere()
    {
        // GH-4888
        var result = plan(declared(m =>
        {
            m.Automation("OpenCase").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened");
            m.Slice("BookAppointment").TriggeredBy(TriggerKind.MessageHandler).Command("BookAppointmentRequest").Emits("AppointmentBooked");
            m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http).Command<ConfirmAppointmentRequest>()
                .Against<Appointment>().Emits<AppointmentConfirmed>();
        }));

        foreach (var file in result.Files) file.Code.ShouldNotContain("NewGuid");
    }

    [Fact]
    public void a_file_that_already_exists_is_reported_and_left_alone()
    {
        var result = plan(
            declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
                .Command<ConfirmAppointmentRequest>()),
            exists: path => path == "ConfirmAppointment.cs");

        result.Files.ShouldBeEmpty();
        result.Notices.Single().Kind.ShouldBe(ScaffoldNoticeKind.Exists);
    }

    [Fact]
    public void a_slice_with_code_behind_it_is_not_declared_only()
    {
        var model = declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command<ConfirmAppointmentRequest>());

        var implemented = EventModelSliceDescriptor.Named("ConfirmAppointment") with
        {
            HandlerType = TypeDescriptor.For(typeof(Appointment))
        };

        var merged = EventModelDescriptor.Merge("Clinic", new[]
        {
            model, new EventModelDescriptor("Clinic", new[] { implemented }).WithProvenance(EventModelProvenance.Derived)
        });

        SliceScaffolder.IsDeclaredOnly(merged.Slices.Single()).ShouldBeFalse();
        plan(merged).Files.ShouldBeEmpty();
    }

    [Fact]
    public void the_scaffolded_handlers_compile()
    {
        var result = plan(declared(m =>
        {
            m.Automation("OpenCase").InDomain("Cases").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened");
            m.Automation("ProposeHomeCheckAppointment").On<HomeCheckAssignmentAccepted>().Command<ProposeAppointment>();
            m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler).Command<ConfirmAppointmentRequest>()
                .Against<Appointment>().Emits<AppointmentConfirmed>().Publishes<AppointmentReminderScheduled>();
            m.Slice("RecordVisit").TriggeredBy(TriggerKind.MessageHandler).Command("RecordVisitRequest")
                .Reads<Appointment>().Produces("VisitRecord");

            // GH-4884, GH-4889, GH-4892: the shapes this round added compile too
            m.Slice("CancelAppointment").TriggeredBy(TriggerKind.Human).Command("CancelAppointmentRequest")
                .Against<Appointment>().Emits("AppointmentCancelled");
            m.Slice("LogCall").TriggeredBy(TriggerKind.MessageHandler).Command("LogCallRequest").Emits("CallLogged");

            // GH-4895: one IEventStream<T> per aggregate
            m.Slice("AcceptHomeCheckAssignment").TriggeredBy(TriggerKind.MessageHandler)
                .Command<AcceptHomeCheckAssignment>().Against<HomeCheck>().Against<VolunteerApplication>()
                .Emits<HomeCheckAssignmentAcceptedEvent>();
            m.View("AppointmentsQueue").From<AppointmentConfirmed>();
        }));

        // LogCall names no aggregate: written, as a TODO, with a warning (GH-4895)
        result.Notices.Where(x => x.Kind != ScaffoldNoticeKind.Wrote && x.Kind != ScaffoldNoticeKind.Edit)
            .Select(x => x.Subject).ShouldBe(new[] { "LogCall" });

        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }
}

/// <summary>Compile scaffolded files against everything this test process has loaded.</summary>
public static class ScaffoldCompilation
{
    public static string[] Errors(IEnumerable<ScaffoldFile> files)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => MetadataReference.CreateFromFile(x.Location));

        var compilation = CSharpCompilation.Create("Scaffolded",
            files.Select(x => CSharpSyntaxTree.ParseText(x.Code, path: x.RelativePath)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString())
            .ToArray();
    }
}

public record ConfirmAppointmentRequest(Guid AppointmentId);

public record AppointmentConfirmed(Guid AppointmentId);

public record AppointmentReminderScheduled(Guid AppointmentId);

public record HomeCheckAssignmentAccepted(Guid HomeCheckId);

public record ProposeAppointment(Guid HomeCheckId);

public class Appointment
{
    public Guid Id { get; set; }
}

public record AppointmentBoard(Guid Id, int Confirmed);

public record AcceptHomeCheckAssignment(Guid HomeCheckId, Guid VolunteerApplicationId);

public record HomeCheckAssignmentAcceptedEvent;

public readonly record struct ScreeningTag(Guid Value);

public record CustomerTag(string Value);

public record ReserveSeat(ScreeningTag Screening, CustomerTag Customer, string Seat);

public record SeatReserved;

public class SeatAvailability
{
    public Guid Id { get; set; }
}

public class HomeCheck
{
    public Guid Id { get; set; }
}

public class VolunteerApplication
{
    public Guid Id { get; set; }
}

// Declared-only is a question only the running application can answer: the declared model and the
// derived chains have to be assembled together first.
public class event_model_scaffold_against_a_host_4832
{
    [Fact]
    public async Task only_the_slices_with_no_code_behind_them_are_scaffolded()
    {
        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Clinic";
                opts.Discovery.DisableConventionalDiscovery().IncludeType<HomeCheckAssignmentAcceptedHandler>();
                opts.Services.AddEventModel("Clinic", m =>
                {
                    // implemented: the handler below joins it (GH-4831)
                    m.Automation("ProposeHomeCheckAppointment").On<HomeCheckAssignmentAccepted>();

                    // declared only
                    m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
                        .Command<ConfirmAppointmentRequest>();
                });
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var model = (await Wolverine.Configuration.EventModeling.WolverineEventModelExport.AssembleSetAsync(
            host.Services, token: TestContext.Current.CancellationToken)).Models.Single();

        var plan = SliceScaffolder.Plan(model, new SliceScaffoldOptions { RootNamespace = "Clinic" });

        plan.Files.Select(x => x.RelativePath).ShouldBe(new[] { "ConfirmAppointment.cs" });
    }
}

public class HomeCheckAssignmentAcceptedHandler
{
    public ProposeAppointment Handle(HomeCheckAssignmentAccepted e) => new(e.HomeCheckId);
}
