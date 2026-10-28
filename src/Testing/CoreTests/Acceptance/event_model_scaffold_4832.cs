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
        Func<Type, string?>? findSource = null, Action<SliceScaffoldOptions>? configure = null)
    {
        var options = new SliceScaffoldOptions
        {
            RootNamespace = "Clinic",
            ResolveType = resolve,
            FileExists = exists ?? (_ => false),
            FindSourceFile = findSource ?? (_ => null)
        };
        configure?.Invoke(options);
        return SliceScaffolder.Plan(model, options);
    }

    [Fact]
    public void a_declared_http_command_becomes_a_store_agnostic_endpoint()
    {
        var result = plan(declared(m => m.InChapter("Scheduling")
            .Slice("ConfirmAppointment").Pattern(SlicePattern.Command).TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>()));

        var file = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "Scheduling", "ConfirmAppointment.cs"));

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
    public void an_http_stream_start_answers_with_the_id_it_assigned()
    {
        // GH-4927: a 201 with the new stream's id, not an empty response the caller cannot follow
        var result = plan(declared(m => m.Slice("OpenAppointment").Pattern(SlicePattern.Command).TriggeredBy(TriggerKind.Http)
            .Command<ProposeAppointment>().StartsStream<Appointment>().Emits<AppointmentConfirmed>()));
        var code = result.Files.Single().Code;

        code.ShouldContain("[WolverinePost(\"/api/open-appointment\")]");
        code.ShouldNotContain("[EmptyResponse]");
        code.ShouldContain("public static (CreationResponse<Guid>, StartStream) Post(ProposeAppointment command)");
        code.ShouldContain("//     return (new CreationResponse<Guid>($\"/api/appointment/{id}\", id), Storage.StartStream<Appointment>(id, new AppointmentConfirmed(...)));");
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

        // GH-5255. The scaffold renders through SourceWriter, so it writes the platform's line endings
        var insert = result.Files.Single(x => x.InsertInto == nameof(AppointmentBoard));
        var code = insert.Code.ReplaceLineEndings("\n");
        code.ShouldContain("""
                                  public static AppointmentBoard Create(AppointmentConfirmed e)
                                  {
                                      // TODO: build the view from AppointmentConfirmed, the first event it sees
                                      throw new NotImplementedException("TODO: AppointmentBoard from AppointmentConfirmed");
                                  }
                                  """.ReplaceLineEndings("\n"));
        code.ShouldNotContain("Apply(AppointmentConfirmed e)");
        code.ShouldContain("public AppointmentBoard Apply(AppointmentReminderScheduled e)");
        code.ShouldContain("return this;");
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

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "OpenCase.cs"));
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

        var aggregate = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "ClinicCase.cs"));
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
        var code = result.Files.Single(x => x.InsertInto is null).Code.ReplaceLineEndings("\n");

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

        // GH-4883: the registration itself, the same call on Marten, Polecat and Fisher
        register.Message.ShouldContain("opts.Events.RegisterTagType<ScreeningTag>(); opts.Events.RegisterTagType<CustomerTag>();");
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

        var projection = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "AppointmentBoardProjection.cs")).Code;
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
    public void every_notice_carries_a_stable_code_and_the_plan_reads_as_json()
    {
        // GH-4928: what Stoat parses -- files with how they are written, notices with a code each
        var result = plan(
            declared(m =>
            {
                m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.MessageHandler)
                    .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>();
                m.Slice("BookSlot").Command<ReserveSeat>();
                m.Command<AcceptHomeCheckAssignment>().TriggeredBy(TriggerKind.MessageHandler)
                    .DeciderModel<SeatAvailability>().Emits<HomeCheckAssignmentAcceptedEvent>();
                m.View<AppointmentBoard>().From<AppointmentConfirmed>().AsMultiStream();
            }),
            findSource: type => $"Domain/{type.Name}.cs");

        result.Notices.ShouldNotBeEmpty();
        result.Notices.ShouldAllBe(x => x.Code != null);
        result.Notices.Select(x => x.Code).ShouldContain(ScaffoldNoticeCodes.UnknownTrigger);
        result.Notices.Select(x => x.Code).ShouldContain(ScaffoldNoticeCodes.DcbWithoutTagMembers);
        result.Notices.Select(x => x.Code).ShouldContain(ScaffoldNoticeCodes.WriteMultiStreamProjection);

        using var json = System.Text.Json.JsonDocument.Parse(SliceScaffolder.ToJson(result, "Clinic", "/out", dryRun: true));
        var root = json.RootElement;
        root.GetProperty("model").GetString().ShouldBe("Clinic");
        root.GetProperty("dryRun").GetBoolean().ShouldBeTrue();

        var files = root.GetProperty("files").EnumerateArray().ToList();
        files.Select(x => x.GetProperty("mode").GetString()).ShouldContain("append");
        files.Select(x => x.GetProperty("mode").GetString()).ShouldContain("insert");

        var notices = root.GetProperty("notices").EnumerateArray().ToList();
        notices.Count.ShouldBe(result.Notices.Count);
        notices.ShouldAllBe(x => x.GetProperty("code").GetString()!.Length > 0);
        notices.ShouldContain(x => x.GetProperty("kind").GetString() == "UnknownTrigger" && x.GetProperty("subject").GetString() == "BookSlot");
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
        var code = result.Files.Single().Code.ReplaceLineEndings("\n");

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

        result.Files.Single(x => x.RelativePath == Path.Combine("Features", "ConfirmAppointment.cs")).Code
            .ShouldContain("public static class ConfirmAppointmentHandler");

        var notice = result.Notices.Single(x => x.Subject == "ConfirmAppointment");
        notice.Kind.ShouldBe(ScaffoldNoticeKind.Wrote);
        notice.Message.ShouldContain("TriggerKind.Human");
        notice.Message.ShouldContain("message handler");
    }

    [Fact]
    public void slices_and_their_aggregates_go_in_a_folder_and_namespace_per_chapter()
    {
        // GH-4891: Features/{Chapter}/ in {Root}.{Chapter}, the layout `bobcat import-event-model` writes
        var result = plan(declared(m => m.InChapter("Volunteering And Home Checks")
            .Automation("OpenCase").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened")));

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "VolunteeringAndHomeChecks", "OpenCase.cs"));
        handler.Code.ShouldContain("namespace Clinic.VolunteeringAndHomeChecks;");

        result.Files.ShouldContain(x => x.RelativePath == Path.Combine("Features", "VolunteeringAndHomeChecks", "ClinicCase.cs"));
    }

    [Fact]
    public void a_domain_is_not_a_folder_or_a_namespace()
    {
        // Bobcat's layout has no folder for a domain, so neither does the scaffold: the file it adds and the
        // ones `bobcat import-event-model` wrote have to agree on where a chapter's types live
        var result = plan(declared(m => m.InDomain("Adoption").InChapter("Volunteering")
            .Automation("OpenCase").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened")));

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "Volunteering", "OpenCase.cs"));
        handler.Code.ShouldContain("namespace Clinic.Volunteering;");
        result.Files.ShouldNotContain(x => x.RelativePath.Contains("Adoption"));
    }

    [Fact]
    public void a_slice_without_a_chapter_goes_in_features_and_the_root_namespace()
    {
        var result = plan(declared(m => m.Automation("OpenCase").On("PatientReferred").StartsStream("ClinicCase").Emits("CaseOpened")));

        var handler = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "OpenCase.cs"));
        handler.Code.ShouldContain("namespace Clinic;");
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
            exists: path => path == Path.Combine("Features", "ConfirmAppointment.cs"));

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
    public void a_human_triggered_slice_merged_with_its_handler_is_not_scaffolded_again()
    {
        // jasperfx#1007: the merge keeps the code's MessageHandler on TriggerKind and moves the board's Human
        // to TriggeredBy -- running the scaffold again over the merged model must leave the slice alone
        var model = declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Human)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>().Emits<AppointmentConfirmed>());

        var implemented = EventModelSliceDescriptor.Named("ConfirmAppointment") with
        {
            HandlerType = TypeDescriptor.For(typeof(Appointment)),
            HandlerMethod = "Handle",
            TriggerKind = TriggerKind.MessageHandler
        };

        var merged = EventModelDescriptor.Merge("Clinic", new[]
        {
            model, new EventModelDescriptor("Clinic", new[] { implemented }).WithProvenance(EventModelProvenance.Derived)
        });

        var slice = merged.Slices.Single();
        slice.TriggerKind.ShouldBe(TriggerKind.MessageHandler);
        slice.TriggeredBy.ShouldBe(TriggerKind.Human);
        slice.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);

        SliceScaffolder.IsDeclaredOnly(slice).ShouldBeFalse();
        var result = plan(merged);
        result.Files.ShouldBeEmpty();
        result.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void a_declared_only_human_slice_still_carries_human_on_its_trigger_kind()
    {
        // nothing has been merged in yet, so the board's Human is still the only trigger there is
        var slice = declared(m => m.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Human)
            .Command<ConfirmAppointmentRequest>()).Slices.Single();

        slice.TriggerKind.ShouldBe(TriggerKind.Human);
        slice.TriggeredBy.ShouldBeNull();
        SliceScaffolder.IsDeclaredOnly(slice).ShouldBeTrue();
    }


    // ---------------------------------------------------------------------------------------------------
    // GH-4975: Query slices (jasperfx#1003)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void an_http_query_of_an_aggregate_becomes_a_get_endpoint_that_reads_it()
    {
        // The default route is the one a scaffolded stream start's Location points at, so a 201 can be followed
        var result = plan(declared(m =>
        {
            m.Query<Appointment>().TriggeredBy(TriggerKind.Http);
            m.Command<ConfirmAppointmentRequest>().TriggeredBy(TriggerKind.MessageHandler).Against<Appointment>()
                .Emits<AppointmentConfirmed>();
        }));

        var code = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "GetAppointment.cs")).Code;
        code.ShouldContain("public static class GetAppointmentEndpoint");
        code.ShouldContain("[WolverineGet(\"/api/appointment/{id}\")]");
        code.ShouldContain("public static Appointment Get([ReadModel] Appointment appointment)");
        code.ShouldContain("return appointment;");
        code.ShouldContain("using Wolverine.Http;");
        code.ShouldContain("using Wolverine.Persistence.EventSourcing;");

        // store-agnostic, like every other shape
        code.ShouldNotContain("ReadAggregate");
        code.ShouldNotContain("Marten");
    }

    [Fact]
    public void an_http_query_uses_the_slices_route_and_route_name_and_loads_a_document_with_entity()
    {
        var model = declared(m => m.Query<AppointmentBoard>("Board").TriggeredBy(TriggerKind.Http));
        var slice = model.Slices.Single() with
        {
            TriggerOrigin = new PublisherOrigin
            {
                HttpMethod = "GET", HttpRoute = "/api/boards/{boardId}", HttpRouteName = "BoardById",
                Label = "GET /api/boards/{boardId}"
            }
        };

        var code = plan(model with { Slices = [slice] }).Files.Single().Code;

        code.ShouldContain("[WolverineGet(\"/api/boards/{boardId}\", RouteName = \"BoardById\")]");
        // AppointmentBoard is no aggregate, so it is a document loaded by the route parameter that names it
        code.ShouldContain("public static AppointmentBoard Get([Entity(\"boardId\")] AppointmentBoard appointmentBoard)");
        code.ShouldContain("using Wolverine.Persistence;");
    }

    [Fact]
    public void a_message_handler_query_answers_a_query_message_carrying_the_identity()
    {
        var result = plan(declared(m => m.Query<Appointment>().TriggeredBy(TriggerKind.MessageHandler)));
        var code = result.Files.Single().Code;

        code.ShouldContain("public record GetAppointment(Guid Id);");
        code.ShouldContain("public static class GetAppointmentHandler");
        code.ShouldContain("public static Appointment Handle(GetAppointment query, [Entity] Appointment appointment)");

        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    [Fact]
    public void a_declared_query_message_is_the_one_the_handler_answers()
    {
        var result = plan(declared(m => m.Query<Appointment>("LookUpAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command("FindAppointment").Against<Appointment>()));
        var code = result.Files.Single(x => x.InsertInto is null).Code;

        code.ShouldContain("public record FindAppointment(Guid Id);");
        code.ShouldContain("public static Appointment Handle(FindAppointment query, [ReadModel] Appointment appointment)");
    }

    [Fact]
    public void a_grpc_query_is_scaffolded_as_the_handler_behind_the_rpc()
    {
        var result = plan(declared(m => m.Query<Appointment>().TriggeredBy(TriggerKind.Grpc)));

        result.Files.Single().Code.ShouldContain("public static Appointment Handle(GetAppointment query, [Entity] Appointment appointment)");
        result.Notices.Single(x => x.Code == ScaffoldNoticeCodes.FileWritten).Message.ShouldContain("Triggered by gRPC");
    }

    [Fact]
    public void a_query_that_reads_nothing_or_has_no_trigger_is_reported()
    {
        var result = plan(declared(m =>
        {
            m.Query("Dashboard").TriggeredBy(TriggerKind.Http);
            m.Query<Appointment>();
        }));

        result.Files.ShouldBeEmpty();
        result.Notices.Single(x => x.Subject == "Dashboard").Code.ShouldBe(ScaffoldNoticeCodes.QueryWithoutReadModel);

        var unknown = result.Notices.Single(x => x.Subject == "GetAppointment");
        unknown.Code.ShouldBe(ScaffoldNoticeCodes.QueryWithoutTrigger);
        unknown.Kind.ShouldBe(ScaffoldNoticeKind.UnknownTrigger);

        using var json = System.Text.Json.JsonDocument.Parse(SliceScaffolder.ToJson(result, "Clinic", "/out", dryRun: true));
        json.RootElement.GetProperty("notices").EnumerateArray().Select(x => x.GetProperty("code").GetString())
            .ShouldBe(new[] { ScaffoldNoticeCodes.QueryWithoutReadModel, ScaffoldNoticeCodes.QueryWithoutTrigger }, ignoreOrder: true);
    }

    [Fact]
    public void an_http_slice_in_an_application_without_wolverine_http_is_reported_not_written()
    {
        var result = plan(declared(m =>
        {
            m.Query<Appointment>().TriggeredBy(TriggerKind.Http);
            m.Command<ConfirmAppointmentRequest>().TriggeredBy(TriggerKind.Http).Against<Appointment>().Emits<AppointmentConfirmed>();
        }), configure: o => o.WolverineHttp = false);

        result.Files.ShouldNotContain(x => x.Code.Contains("Wolverine.Http"));
        result.Notices.Where(x => x.Code == ScaffoldNoticeCodes.HttpEndpointsUnavailable).Select(x => x.Subject)
            .ShouldBe(new[] { nameof(ConfirmAppointmentRequest), "GetAppointment" }, ignoreOrder: true);
    }

    [Fact]
    public void the_scaffolded_query_handlers_compile()
    {
        var result = plan(declared(m =>
        {
            m.Query<Appointment>().TriggeredBy(TriggerKind.MessageHandler);
            m.Query<AppointmentBoard>("Board").TriggeredBy(TriggerKind.Human);
            m.Query("Visit").TriggeredBy(TriggerKind.MessageHandler).Reads("VisitRecord").Reads<Appointment>();
        }));

        result.Files.Count.ShouldBe(3);
        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // GH-4883: strong-typed identifiers
    // ---------------------------------------------------------------------------------------------------

    private static EventModelDescriptor ticketing(Action<EventModelBuilder>? more = null) => declared(m =>
    {
        more?.Invoke(m);
        m.Slice("OpenTicket").TriggeredBy(TriggerKind.MessageHandler).Command("OpenTicketRequest")
            .StartsStream("Ticket").Emits("TicketOpened");
        m.Slice("CloseTicket").TriggeredBy(TriggerKind.MessageHandler).Command("CloseTicketRequest")
            .Against("Ticket").Emits("TicketClosed");
        m.Query("GetTicket").TriggeredBy(TriggerKind.MessageHandler).Reads("Ticket");
    });

    [Fact]
    public void without_the_flag_an_aggregate_is_identified_by_a_guid()
    {
        var result = plan(ticketing());

        result.Files.ShouldNotContain(x => x.RelativePath.EndsWith("TicketId.cs"));
        result.Files.Single(x => x.RelativePath == Path.Combine("Features", "Ticket.cs")).Code.ShouldContain("public Guid Id { get; set; }");
        // A stub command carries the stream it identifies as a lone Id, in Guid mode too
        result.Files.Single(x => x.RelativePath.EndsWith("CloseTicket.cs")).Code.ShouldContain("public record CloseTicketRequest(Guid Id);");
        result.Files.Single(x => x.RelativePath.EndsWith("CloseTicket.cs")).Code.ShouldContain("public record TicketClosed;");
        result.Files.Single(x => x.RelativePath.EndsWith("OpenTicket.cs")).Code.ShouldContain("public record OpenTicketRequest;");
    }

    [Fact]
    public void the_flag_gives_every_aggregate_a_strong_typed_id_threaded_through_every_shape()
    {
        var result = plan(ticketing(), configure: o => o.StrongTypedIds = true);

        // The id: a plain record struct over a Guid, bindable from a route
        var id = result.Files.Single(x => x.RelativePath == Path.Combine("Features", "TicketId.cs")).Code;
        id.ShouldContain("namespace Clinic;");
        id.ShouldContain("[JsonConverter(typeof(TicketId.TicketIdJsonConverter))]");
        id.ShouldContain("public readonly record struct TicketId(Guid Value) : IParsable<TicketId>");
        id.ShouldContain("public static TicketId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));");
        id.ShouldContain("public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out TicketId result)");
        id.ShouldContain("public override string ToString() => Value.ToString();");
        id.ShouldContain("public sealed class TicketIdJsonConverter : JsonConverter<TicketId>");
        id.ShouldContain("public override TicketId ReadAsPropertyName(");

        // The aggregate
        result.Files.Single(x => x.RelativePath == Path.Combine("Features", "Ticket.cs")).Code
            .ShouldContain("public TicketId Id { get; set; }");

        // A stream start mints it, and hands the store the Guid it wraps; the command carries no id
        var open = result.Files.Single(x => x.RelativePath.EndsWith("OpenTicket.cs")).Code;
        open.ShouldContain("public record OpenTicketRequest;");
        open.ShouldContain("public record TicketOpened;");
        open.ShouldContain("//     var id = new TicketId(Guid.CreateVersion7());");
        open.ShouldContain("//     return Storage.StartStream<Ticket>(id.Value, new TicketOpened(...));");

        // A command against it carries it as a lone Id; a single-stream slice's events need not
        var close = result.Files.Single(x => x.RelativePath.EndsWith("CloseTicket.cs")).Code;
        close.ShouldContain("public record CloseTicketRequest(TicketId Id);");
        close.ShouldContain("public record TicketClosed;");
        close.ShouldContain("public static TicketClosed? Handle(CloseTicketRequest command, [WriteModel] Ticket ticket)");

        // A query of it is asked by it
        result.Files.Single(x => x.RelativePath.EndsWith("GetTicket.cs")).Code
            .ShouldContain("public record GetTicket(TicketId Id);");

        result.Notices.ShouldNotContain(x => x.Kind == ScaffoldNoticeKind.Warning);
        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    [Fact]
    public void an_http_stream_start_answers_with_the_strong_typed_id()
    {
        var result = plan(declared(m => m.Slice("OpenTicket").TriggeredBy(TriggerKind.Http).Command("OpenTicketRequest")
            .StartsStream("Ticket").Emits("TicketOpened")), configure: o => o.StrongTypedIds = true);
        var code = result.Files.Single(x => x.RelativePath.EndsWith("OpenTicket.cs")).Code;

        code.ShouldContain("public static (CreationResponse<TicketId>, StartStream) Post(OpenTicketRequest command)");
        code.ShouldContain("//     return (new CreationResponse<TicketId>($\"/api/ticket/{id.Value}\", id), Storage.StartStream<Ticket>(id.Value, new TicketOpened(...)));");
    }

    [Fact]
    public void a_declared_identity_wins_over_the_flag()
    {
        // IdentifiedBy<string>(): a natural key, which the handler cannot make up -- the command carries it
        var result = plan(ticketing(m => m.Aggregate("Ticket").IdentifiedBy<string>()), configure: o => o.StrongTypedIds = true);

        result.Files.ShouldNotContain(x => x.RelativePath.EndsWith("TicketId.cs"));
        result.Files.Single(x => x.RelativePath == Path.Combine("Features", "Ticket.cs")).Code.ShouldContain("public string Id { get; set; }");

        var open = result.Files.Single(x => x.RelativePath.EndsWith("OpenTicket.cs")).Code;
        open.ShouldContain("public record OpenTicketRequest(string Id);");
        open.ShouldContain("//     var id = command.Id;");
        open.ShouldContain("//     return Storage.StartStream<Ticket>(id, new TicketOpened(...));");
        open.ShouldNotContain("CreateVersion7");

        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    [Fact]
    public void a_declared_strong_typed_natural_key_unwraps_its_value()
    {
        var result = plan(declared(m =>
        {
            m.Aggregate("Ticket").IdentifiedBy<TicketCode>();
            m.Slice("OpenTicket").TriggeredBy(TriggerKind.MessageHandler).Command("OpenTicketRequest")
                .StartsStream("Ticket").Emits("TicketOpened");
        }));

        var open = result.Files.Single(x => x.RelativePath.EndsWith("OpenTicket.cs")).Code;
        open.ShouldContain("public record OpenTicketRequest(TicketCode Id);");
        open.ShouldContain("//     return Storage.StartStream<Ticket>(id.Value, new TicketOpened(...));");
        result.Files.Single(x => x.RelativePath == Path.Combine("Features", "Ticket.cs")).Code.ShouldContain("public TicketCode Id { get; set; }");
    }

    [Fact]
    public void a_declared_identity_by_name_is_written_even_without_the_flag()
    {
        var result = plan(ticketing(m => m.Aggregate("Ticket").IdentifiedBy("TicketNumber")));

        result.Files.Single(x => x.RelativePath == Path.Combine("Features", "TicketNumber.cs")).Code
            .ShouldContain("public readonly record struct TicketNumber(Guid Value)");
        result.Files.Single(x => x.RelativePath.EndsWith("CloseTicket.cs")).Code
            .ShouldContain("public record CloseTicketRequest(TicketNumber Id);");
    }

    [Fact]
    public void an_existing_aggregate_keeps_the_identity_it_has()
    {
        // Appointment exists with a Guid Id: the flag does not contradict the code
        var result = plan(declared(m => m.Slice("CancelAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command("CancelAppointmentRequest").Against<Appointment>().Emits("AppointmentCancelled")),
            configure: o => o.StrongTypedIds = true);

        result.Files.ShouldNotContain(x => x.RelativePath.EndsWith("AppointmentId.cs"));
        result.Files.Single(x => x.InsertInto is null).Code.ShouldContain("public record CancelAppointmentRequest(Guid Id);");

        // ...and says so, once
        var kept = result.Notices.Single(x => x.Code == ScaffoldNoticeCodes.ExistingAggregateKeepsIdentity);
        kept.Message.ShouldContain("Appointment keeps Guid Id because the class already exists");
        kept.Message.ShouldContain("IdentifiedBy<AppointmentId>()");

        using var json = System.Text.Json.JsonDocument.Parse(SliceScaffolder.ToJson(result, "Clinic", "/out", dryRun: true));
        json.RootElement.GetProperty("notices").EnumerateArray()
            .ShouldContain(x => x.GetProperty("code").GetString() == ScaffoldNoticeCodes.ExistingAggregateKeepsIdentity);
    }

    [Fact]
    public void without_the_flag_an_existing_aggregate_says_nothing_about_its_identity()
    {
        var result = plan(declared(m => m.Slice("CancelAppointment").TriggeredBy(TriggerKind.MessageHandler)
            .Command("CancelAppointmentRequest").Against<Appointment>().Emits("AppointmentCancelled")));

        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.ExistingAggregateKeepsIdentity);
    }

    [Fact]
    public void a_guid_command_against_two_aggregates_carries_an_id_per_stream_and_so_do_its_events()
    {
        var result = plan(declared(m => m.Slice("Transfer").TriggeredBy(TriggerKind.MessageHandler)
            .Command("TransferRequest").Against("Wallet").Against("Vault").Emits("Transferred")));

        var code = result.Files.Single(x => x.RelativePath.EndsWith("Transfer.cs")).Code;
        code.ShouldContain("public record TransferRequest(Guid WalletId, Guid VaultId);");
        code.ShouldContain("public record Transferred(Guid WalletId, Guid VaultId);");
        code.ShouldContain("[WriteModel(nameof(TransferRequest.WalletId))] IEventStream<Wallet> walletStream");

        // The stub carries the member, so there is nothing to warn about
        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.MissingStreamIdMember);
        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    [Fact]
    public void a_dcb_stub_carries_the_decider_models_strong_typed_tag_and_queries_by_it()
    {
        var result = plan(declared(m => m.Command("ReserveSlot").TriggeredBy(TriggerKind.MessageHandler)
            .DeciderModel("SlotLedger").Emits("SlotReserved")), configure: o => o.StrongTypedIds = true);

        var code = result.Files.Single(x => x.RelativePath.EndsWith("ReserveSlot.cs")).Code;
        code.ShouldContain("public record ReserveSlot(SlotLedgerId Id);");
        code.ShouldContain("public record SlotReserved(SlotLedgerId Id);");
        code.ShouldContain("return EventTagQuery.For(command.Id);");

        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.DcbWithoutTagMembers);
        result.Notices.Single(x => x.Code == ScaffoldNoticeCodes.RegisterDcbTags).Message
            .ShouldContain("opts.Events.RegisterTagType<SlotLedgerId>();");
        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
    }

    [Fact]
    public void the_generated_strong_typed_id_is_parsable_and_a_bare_guid_in_json()
    {
        var result = plan(ticketing(), configure: o => o.StrongTypedIds = true);
        var assembly = ScaffoldCompilation.Load(result.Files);
        var idType = assembly.GetType("Clinic.TicketId")!;

        var guid = Guid.NewGuid();
        var id = Activator.CreateInstance(idType, guid)!;

        // ToString is the Guid, for routes and Location headers
        id.ToString().ShouldBe(guid.ToString());

        // IParsable<T>: what Wolverine.Http's binding calls
        idType.GetInterfaces().ShouldContain(typeof(IParsable<>).MakeGenericType(idType));
        var tryParse = idType.GetMethod("TryParse", [typeof(string), typeof(IFormatProvider), idType.MakeByRefType()])!;
        var arguments = new object?[] { guid.ToString(), System.Globalization.CultureInfo.InvariantCulture, null };
        ((bool)tryParse.Invoke(null, arguments)!).ShouldBeTrue();
        arguments[2].ShouldBe(id);
        ((bool)tryParse.Invoke(null, new object?[] { "not a guid", null, null })!).ShouldBeFalse();

        // System.Text.Json: the bare Guid, both ways, also as a dictionary key and inside a message
        var json = System.Text.Json.JsonSerializer.Serialize(id, idType);
        json.ShouldBe($"\"{guid}\"");
        System.Text.Json.JsonSerializer.Deserialize(json, idType).ShouldBe(id);

        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(idType, typeof(int));
        var dictionary = (System.Collections.IDictionary)Activator.CreateInstance(dictionaryType)!;
        dictionary[id] = 3;
        var keyed = System.Text.Json.JsonSerializer.Serialize(dictionary, dictionaryType);
        keyed.ShouldBe($"{{\"{guid}\":3}}");
        ((System.Collections.IDictionary)System.Text.Json.JsonSerializer.Deserialize(keyed, dictionaryType)!)[id].ShouldBe(3);

        var command = Activator.CreateInstance(assembly.GetType("Clinic.CloseTicketRequest")!, id)!;
        System.Text.Json.JsonSerializer.Serialize(command, command.GetType(),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            .ShouldBe($"{{\"id\":\"{guid}\"}}");

        // ...and through Wolverine's own message serializer
        var serializer = new Wolverine.Runtime.Serialization.SystemTextJsonSerializer(
            Wolverine.Runtime.Serialization.SystemTextJsonSerializer.DefaultOptions());
        var bytes = serializer.WriteMessage(command);
        System.Text.Encoding.UTF8.GetString(bytes).ShouldContain($"\"{guid}\"");
        System.Text.Encoding.UTF8.GetString(bytes).ShouldNotContain("\"value\"", Case.Insensitive);
        serializer.ReadFromData(command.GetType(), new Wolverine.Envelope { Data = bytes }).ShouldBe(command);
    }

    [Fact]
    public void a_command_against_two_strong_typed_aggregates_carries_both_ids()
    {
        var result = plan(declared(m => m.Slice("Transfer").TriggeredBy(TriggerKind.MessageHandler)
            .Command("TransferRequest").Against("Wallet").Against("Vault").Emits("Transferred")),
            configure: o => o.StrongTypedIds = true);

        var code = result.Files.Single(x => x.RelativePath.EndsWith("Transfer.cs")).Code;
        code.ShouldContain("public record TransferRequest(WalletId WalletId, VaultId VaultId);");
        code.ShouldContain("public record Transferred(WalletId WalletId, VaultId VaultId);");
        code.ShouldContain("[WriteModel(nameof(TransferRequest.WalletId))] IEventStream<Wallet> walletStream");
        result.Notices.ShouldNotContain(x => x.Code == ScaffoldNoticeCodes.MissingStreamIdMember);

        ScaffoldCompilation.Errors(result.Files).ShouldBeEmpty();
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

    /// <summary>Compile the scaffolded files into an assembly and load it, to exercise what the scaffold wrote.</summary>
    public static System.Reflection.Assembly Load(IEnumerable<ScaffoldFile> files)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => MetadataReference.CreateFromFile(x.Location));

        var compilation = CSharpCompilation.Create($"Scaffolded{Guid.NewGuid():N}",
            files.Select(x => CSharpSyntaxTree.ParseText(x.Code, path: x.RelativePath)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
        return System.Reflection.Assembly.Load(stream.ToArray());
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

// GH-4883: a strong-typed natural key
public readonly record struct TicketCode(string Value);

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

        plan.Files.Select(x => x.RelativePath).ShouldBe(new[] { Path.Combine("Features", "ConfirmAppointment.cs") });
    }
}

public class HomeCheckAssignmentAcceptedHandler
{
    public ProposeAppointment Handle(HomeCheckAssignmentAccepted e) => new(e.HomeCheckId);
}
