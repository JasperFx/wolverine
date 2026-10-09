using System.Reflection;
using JasperFx.Events.EventModeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Acceptance.EventModel4916;

// GH-4916: every EventModelDefinition in the application assembly is registered from the generated
// manifest, with no AddEventModel<T>() per definition. The definitions live in an assembly compiled here,
// with the manifest JasperFx.SourceGenerator would write, so they reach no other test's host.
public class discovered_event_model_definitions_4916
{
    private static readonly Lazy<Assembly> Clinic = new(compile);

    private static Assembly compile()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using JasperFx.Events.EventModeling;

            namespace Clinic
            {
                public record ConfirmAppointment(Guid Id);
                public record AppointmentConfirmed(Guid Id);
                public record ApplyToVolunteer(Guid Id);
                public record VolunteerApplicationSubmitted(Guid Id);

                public class BookingAppointmentsModel : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model)
                    {
                        model.InChapter("BookingAppointments");
                        model.Command<ConfirmAppointment>().Emits<AppointmentConfirmed>();
                    }
                }

                public class VolunteeringModel : EventModelDefinition
                {
                    public override void Configure(EventModelBuilder model)
                    {
                        model.InChapter("Volunteering");
                        model.Command<ApplyToVolunteer>().Emits<VolunteerApplicationSubmitted>();
                    }
                }
            }

            namespace JasperFx.Generated
            {
                internal static class DiscoveredEventModels
                {
                    public static IReadOnlyList<Type> DefinitionTypes { get; } =
                        new[] { typeof(Clinic.BookingAppointmentsModel), typeof(Clinic.VolunteeringModel) };
                }
            }
            """;

        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create("ClinicDefinitions" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            trusted.Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))
                .Append(MetadataReference.CreateFromFile(typeof(EventModelDefinition).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        emitted.Success.ShouldBeTrue(string.Join(Environment.NewLine, emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    private static IHost host(Action<WolverineOptions>? configure = null, Action<IServiceCollection>? services = null)
        => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Clinic";
                opts.ApplicationAssembly = Clinic.Value;
                configure?.Invoke(opts);
            })
            .ConfigureServices(x => services?.Invoke(x))
            .Build();

    private static Type definition(string name) => Clinic.Value.GetType("Clinic." + name)!;

    [Fact]
    public async Task every_definition_in_the_manifest_joins_the_application_model()
    {
        using var built = host();

        var model = (await EventModelDiscovery.AssembleAsync(built.Services, TestContext.Current.CancellationToken)).Single(x => x.Name == "Clinic");

        model.Slices.Select(x => x.Name).ShouldContain("ConfirmAppointment");
        model.Slices.Select(x => x.Name).ShouldContain("ApplyToVolunteer");
        model.Slices.Single(x => x.Name == "ApplyToVolunteer").Chapter.ShouldBe("Volunteering");
    }

    [Fact]
    public async Task a_definition_registered_by_hand_is_not_registered_twice()
    {
        using var built = host(services: x => x.AddEventModel(definition("VolunteeringModel")));

        var discovered = built.Services.GetServices<IEventModelDefinitionSource>().OfType<DiscoveredEventModelDefinitions>().Single();
        var model = await discovered.TryCreateAsync(built.Services, TestContext.Current.CancellationToken);

        // Only the one nobody registered comes from the manifest
        model!.Slices.Select(x => x.Name).ShouldBe(["ConfirmAppointment"]);
    }

    [Fact]
    public async Task the_option_turns_it_off()
    {
        using var built = host(opts => opts.AutoRegisterEventModelDefinitions = false);

        var discovered = built.Services.GetServices<IEventModelDefinitionSource>().OfType<DiscoveredEventModelDefinitions>().Single();
        (await discovered.TryCreateAsync(built.Services, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void an_assembly_with_no_manifest_contributes_nothing_and_is_never_scanned()
    {
        var options = new WolverineOptions { ApplicationAssembly = typeof(discovered_event_model_definitions_4916).Assembly };

        DiscoveredEventModelDefinitions.DefinitionTypes(options).ShouldBeEmpty();
    }
}
