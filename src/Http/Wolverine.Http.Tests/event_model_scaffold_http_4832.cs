using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Wolverine.Configuration.EventModeling.Scaffolding;

namespace Wolverine.Http.Tests.EventModel4832;

// GH-4832: a declared HTTP Command slice scaffolds to a Wolverine.HTTP endpoint that compiles as written
public class event_model_scaffold_http_4832
{
    [Fact]
    public void the_scaffolded_endpoint_compiles()
    {
        var builder = new EventModelBuilder();
        builder.InDomain("Scheduling");
        builder.Slice("ConfirmAppointment").TriggeredBy(TriggerKind.Http)
            .Command<ConfirmAppointmentRequest>().Against<Appointment>()
            .Emits<AppointmentConfirmed>().Publishes<AppointmentReminderScheduled>();
        builder.Slice("BookAppointment").TriggeredBy(TriggerKind.Http)
            .Command("BookAppointmentRequest").StartsStream<Appointment>().Emits("AppointmentBooked");

        var plan = SliceScaffolder.Plan(builder.Build("Clinic").WithProvenance(EventModelProvenance.Declared),
            new SliceScaffoldOptions
            {
                RootNamespace = "Clinic",
                ResolveType = descriptor => typeof(event_model_scaffold_http_4832).Assembly.GetTypes()
                    .FirstOrDefault(x => x.Namespace == typeof(event_model_scaffold_http_4832).Namespace &&
                                         x.Name == descriptor.Name)
            });

        var endpoint = plan.Files.Single(x => x.RelativePath.EndsWith("ConfirmAppointment.cs")).Code;
        endpoint.ShouldContain("[WolverinePost(\"/api/confirm-appointment\")]");
        endpoint.ShouldContain("""
                                   public static (EventsToAppend, OutgoingMessages) Post(
                                           ConfirmAppointmentRequest command,
                                           [WriteModel] Appointment appointment)
                                   """.ReplaceLineEndings("\n"));

        var booking = plan.Files.Single(x => x.RelativePath.EndsWith("BookAppointment.cs")).Code;
        // GH-4927: an HTTP stream start answers 201 with the id the endpoint assigned
        booking.ShouldContain("public static (CreationResponse<Guid>, StartStream) Post(BookAppointmentRequest command)");

        compile(plan).ShouldBeEmpty();
    }

    [Fact]
    public void the_scaffolded_get_endpoints_compile()
    {
        // GH-4975: a Query over HTTP is a GET that reads an aggregate ([ReadModel]) or a document ([Entity])
        var builder = new EventModelBuilder();
        builder.Query<Appointment>().TriggeredBy(TriggerKind.Http);
        builder.Query<AppointmentCard>("Card").TriggeredBy(TriggerKind.Http);
        builder.Command<ConfirmAppointmentRequest>().TriggeredBy(TriggerKind.Http).Against<Appointment>()
            .Emits<AppointmentConfirmed>();

        var plan = planFor(builder, strongTypedIds: false);

        var get = plan.Files.Single(x => x.RelativePath.EndsWith("GetAppointment.cs")).Code;
        get.ShouldContain("[WolverineGet(\"/api/appointment/{id}\")]");
        get.ShouldContain("public static Appointment Get([ReadModel] Appointment appointment)");

        plan.Files.Single(x => x.RelativePath.EndsWith("Card.cs")).Code
            .ShouldContain("public static AppointmentCard Get([Entity] AppointmentCard appointmentCard)");

        compile(plan).ShouldBeEmpty();
    }

    [Fact]
    public void the_scaffolded_strong_typed_endpoints_compile()
    {
        // GH-4883: --strong-typed-ids, through a stream start's 201, a command and a GET
        var builder = new EventModelBuilder();
        builder.Slice("OpenTicket").TriggeredBy(TriggerKind.Http).Command("OpenTicketRequest")
            .StartsStream("Ticket").Emits("TicketOpened");
        builder.Slice("CloseTicket").TriggeredBy(TriggerKind.Http).Command("CloseTicketRequest")
            .Against("Ticket").Emits("TicketClosed");
        builder.Query("GetTicket").TriggeredBy(TriggerKind.Http).Reads("Ticket");

        var plan = planFor(builder, strongTypedIds: true);

        plan.Files.Single(x => x.RelativePath.EndsWith("OpenTicket.cs")).Code
            .ShouldContain("public static (CreationResponse<TicketId>, StartStream) Post(OpenTicketRequest command)");
        plan.Files.Single(x => x.RelativePath.EndsWith("GetTicket.cs")).Code
            .ShouldContain("public static Ticket Get([ReadModel] Ticket ticket)");

        compile(plan).ShouldBeEmpty();
    }

    private static ScaffoldPlan planFor(EventModelBuilder builder, bool strongTypedIds)
        => SliceScaffolder.Plan(builder.Build("Clinic").WithProvenance(EventModelProvenance.Declared),
            new SliceScaffoldOptions
            {
                RootNamespace = "Clinic",
                StrongTypedIds = strongTypedIds,
                ResolveType = descriptor => typeof(event_model_scaffold_http_4832).Assembly.GetTypes()
                    .FirstOrDefault(x => x.Namespace == typeof(event_model_scaffold_http_4832).Namespace &&
                                         x.Name == descriptor.Name)
            });

    private static string[] compile(ScaffoldPlan plan)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => MetadataReference.CreateFromFile(x.Location));

        var compilation = CSharpCompilation.Create("Scaffolded",
            plan.Files.Select(x => CSharpSyntaxTree.ParseText(x.Code, path: x.RelativePath)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString()).ToArray();
    }
}

public record AppointmentCard(Guid Id, string Summary);

public record ConfirmAppointmentRequest(Guid AppointmentId);

public record AppointmentConfirmed(Guid AppointmentId);

public record AppointmentReminderScheduled(Guid AppointmentId);

public class Appointment
{
    public Guid Id { get; set; }
}
