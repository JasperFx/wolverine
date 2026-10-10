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

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => MetadataReference.CreateFromFile(x.Location));

        var compilation = CSharpCompilation.Create("Scaffolded",
            plan.Files.Select(x => CSharpSyntaxTree.ParseText(x.Code, path: x.RelativePath)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(x => x.Severity == DiagnosticSeverity.Error)
            .Select(x => x.ToString()).ShouldBeEmpty();
    }
}

public record ConfirmAppointmentRequest(Guid AppointmentId);

public record AppointmentConfirmed(Guid AppointmentId);

public record AppointmentReminderScheduled(Guid AppointmentId);

public class Appointment
{
    public Guid Id { get; set; }
}
