using CoreTests.Acceptance.EventModel5004;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Xunit;

// A hand-written stand-in for the manifest Bobcat.Generators writes into a spec project (bobcat#449), so
// CoreTests is itself a "spec assembly" its own path can be handed to --specs. The real, generated manifest
// is exercised end to end in IncidentService.Tests.
[assembly: SpecificationManifest(typeof(SpecManifest5004))]

namespace CoreTests.Acceptance.EventModel5004;

// GH-5004 (jasperfx#997): --specs <assembly> on event-model and describe-event-model links the assembled
// model's slices to the specifications a spec assembly's manifest lists.
public class event_model_specs_5004
{
    private static string specAssembly => typeof(SpecManifest5004).Assembly.Location;

    private static IHostBuilder hostBuilder() => Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.ServiceName = "specs-5004";
            opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(ScheduleVisitHandler))
                .IncludeType(typeof(KennelAdmitPetHandler))
                .IncludeType(typeof(ClinicAdmitPetHandler));
        });

    private static async Task<(bool Succeeded, string Output, EventModelDescriptor? Model)> exportAsync(
        params string[] specs)
    {
        var json = Path.Combine(Path.GetTempPath(), $"event-model-5004-{Guid.NewGuid():N}.json");
        var input = new EventModelInput { HostBuilder = hostBuilder(), JsonFlag = json, SpecsFlag = specs.ToList() };

        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);

        try
        {
            var succeeded = await new EventModelCommand().Execute(input);
            var model = File.Exists(json) ? WolverineEventModelExport.FromJson(await File.ReadAllTextAsync(json)) : null;
            return (succeeded, output.ToString(), model);
        }
        finally
        {
            Console.SetOut(original);
            File.Delete(json);
        }
    }

    [Fact]
    public async Task the_export_links_slices_to_the_specifications_that_exercise_their_command()
    {
        var (succeeded, _, model) = await exportAsync(specAssembly);

        succeeded.ShouldBeTrue();
        var slice = model!.Slices.Single(x => x.CommandType?.Name == nameof(ScheduleVisit));
        slice.Specifications.Select(x => x.Identity).ShouldBe(["Scheduling a visit/books the slot"]);
    }

    [Fact]
    public async Task a_command_several_slices_handle_is_a_hotspot_rather_than_a_guess()
    {
        var (_, _, model) = await exportAsync(specAssembly);

        model!.Slices.Where(x => x.CommandType?.Name == nameof(AdmitPet)).Count().ShouldBe(2);
        model.Slices.Where(x => x.CommandType?.Name == nameof(AdmitPet))
            .ShouldAllBe(x => x.Specifications.Count == 0);

        model.Hotspots.ShouldContain(x =>
            x.Origin == HotspotOrigin.UnresolvedSpecification && x.Text.Contains("Admitting a pet/admits"));
    }

    [Fact]
    public async Task without_specs_nothing_is_linked()
    {
        var (succeeded, _, model) = await exportAsync();

        succeeded.ShouldBeTrue();
        model!.Slices.ShouldAllBe(x => x.Specifications.Count == 0);
    }

    [Fact]
    public async Task a_path_that_does_not_exist_fails_the_command_and_names_the_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-specs-5004.dll");

        var (succeeded, output, _) = await exportAsync(missing);

        succeeded.ShouldBeFalse();
        output.ShouldContain(missing);
    }

    [Fact]
    public async Task an_assembly_with_no_manifest_is_a_warning_not_a_failure()
    {
        var noManifest = typeof(ShouldlyConfiguration).Assembly.Location;

        var (succeeded, output, model) = await exportAsync(noManifest);

        succeeded.ShouldBeTrue();
        output.ShouldContain("carries no specification manifest");
        output.ShouldContain(noManifest);
        model.ShouldNotBeNull();
    }

    [Fact]
    public async Task describe_event_model_fails_on_a_missing_path_too()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-specs-5004.dll");
        var input = new DescribeEventModelInput { HostBuilder = hostBuilder(), SpecsFlag = [missing] };

        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);

        bool succeeded;
        try
        {
            succeeded = await new DescribeEventModelCommand().Execute(input);
        }
        finally
        {
            Console.SetOut(original);
        }

        succeeded.ShouldBeFalse();
        output.ToString().ShouldContain(missing);
    }

    [Fact]
    public void a_selected_model_is_the_only_one_linked()
    {
        // --model on describe-event-model and --name on event-model: the other models are left as assembled
        static EventModelDescriptor modelNamed(string name) => new(name, [
            EventModelSliceDescriptor.Named("ScheduleVisit") with
            {
                CommandType = TypeDescriptor.For(typeof(ScheduleVisit))
            }
        ]);

        var set = EventModelSetDescriptor.For("specs-5004", [modelNamed("Clinic"), modelNamed("Kennel")]);
        var specifications = WolverineEventModelExport.LoadSpecifications([specAssembly]);

        var linked = WolverineEventModelExport.LinkSpecifications(set, specifications, "Kennel");

        linked.Find("Kennel")!.Slices.Single().Specifications.Select(x => x.Identity)
            .ShouldBe(["Scheduling a visit/books the slot"]);
        linked.Find("Clinic")!.Slices.Single().Specifications.ShouldBeEmpty();

        // ...and with no selection, every model is linked
        WolverineEventModelExport.LinkSpecifications(set, specifications).Models
            .ShouldAllBe(x => x.Slices.Single().Specifications.Count == 1);
    }
}

#region sample types for GH-5004

public static class SpecManifest5004
{
    public static IReadOnlyList<SpecificationBindingDescriptor> All { get; } =
    [
        new("Scheduling a visit/books the slot") { CommandType = TypeDescriptor.For(typeof(ScheduleVisit)) },
        new("Admitting a pet/admits") { CommandType = TypeDescriptor.For(typeof(AdmitPet)) }
    ];
}

public record ScheduleVisit(Guid PetId);

public record VisitScheduled(Guid PetId);

public record AdmitPet(Guid PetId);

public record PetAdmitted(Guid PetId);

public static class ScheduleVisitHandler
{
    public static VisitScheduled Handle(ScheduleVisit command) => new(command.PetId);
}

// Two modules handle the same command, so a spec naming only the command cannot choose between them
public static class KennelAdmitPetHandler
{
    public static PetAdmitted Handle(AdmitPet command) => new(command.PetId);
}

public static class ClinicAdmitPetHandler
{
    public static PetAdmitted Handle(AdmitPet command) => new(command.PetId);
}

#endregion
