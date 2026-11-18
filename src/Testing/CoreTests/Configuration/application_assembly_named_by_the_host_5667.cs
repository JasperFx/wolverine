using System.Reflection;
using JasperFx;
using Microsoft.Extensions.Hosting.Internal;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5667: under WebApplicationFactory the entry assembly is the test project, so Wolverine adopted it (or an
/// earlier host's pin) for handler discovery. IHostEnvironment.ApplicationName names the application there, and is
/// now used when nothing named the application assembly explicitly. The end to end case is
/// application_assembly_under_web_application_factory in Wolverine.Http.Tests.
/// </summary>
public class application_assembly_named_by_the_host_5667
{
    private static readonly Assembly Module1Assembly = typeof(Module1.Module1Extension).Assembly;
    private static readonly Assembly ThisTestAssembly = typeof(application_assembly_named_by_the_host_5667).Assembly;

    // Not the entry assembly, not a test runner, and never the process-wide pin: no host is registered from Shouldly
    private static readonly Assembly ExplicitlyChosen = typeof(ShouldAssertException).Assembly;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void no_application_name_names_nothing(string? applicationName)
    {
        WolverineOptions.AssemblyNamedByHost(applicationName, ThisTestAssembly).ShouldBeNull();
    }

    [Fact]
    public void the_entry_assemblys_own_name_is_only_the_default()
    {
        // Every host that isn't told otherwise names itself after the entry assembly: console hosts, dotnet run,
        // and the codegen and describe commands. Those keep the resolution they already had
        WolverineOptions.AssemblyNamedByHost(ThisTestAssembly.GetName().Name, ThisTestAssembly).ShouldBeNull();
        WolverineOptions.AssemblyNamedByHost(ThisTestAssembly.GetName().Name!.ToUpperInvariant(), ThisTestAssembly)
            .ShouldBeNull();
    }

    [Fact]
    public void a_name_that_loads_no_assembly_is_ignored()
    {
        // WebApplicationOptions.ApplicationName can be any display name
        WolverineOptions.AssemblyNamedByHost("Billing Service", ThisTestAssembly).ShouldBeNull();
        WolverineOptions.AssemblyNamedByHost("No.Such.Assembly.Anywhere", ThisTestAssembly).ShouldBeNull();
    }

    [Fact]
    public void a_test_runner_is_never_adopted_whatever_the_host_says()
    {
        WolverineOptions.AssemblyNamedByHost(typeof(FactAttribute).Assembly.GetName().Name, ThisTestAssembly)
            .ShouldBeNull();
    }

    [Fact]
    public void a_name_other_than_the_entry_assembly_names_that_assembly()
    {
        // What WebApplicationFactory does: the application's assembly, while the test project is the entry assembly
        WolverineOptions.AssemblyNamedByHost(Module1Assembly.GetName().Name, ThisTestAssembly)
            .ShouldBe(Module1Assembly);
    }

    [Fact]
    public void what_counts_as_a_guess_from_jasperfx()
    {
        WolverineOptions.IsGuessedApplicationAssembly(null, null, ThisTestAssembly).ShouldBeTrue();

        // The process-wide pin, and the entry assembly JasperFx falls back to
        WolverineOptions.IsGuessedApplicationAssembly(Module1Assembly, Module1Assembly, ThisTestAssembly).ShouldBeTrue();
        WolverineOptions.IsGuessedApplicationAssembly(ThisTestAssembly, Module1Assembly, ThisTestAssembly).ShouldBeTrue();
        WolverineOptions.IsGuessedApplicationAssembly(typeof(FactAttribute).Assembly, Module1Assembly, ThisTestAssembly)
            .ShouldBeTrue();

        // Anything else was chosen on purpose
        WolverineOptions.IsGuessedApplicationAssembly(ExplicitlyChosen, Module1Assembly, ThisTestAssembly).ShouldBeFalse();
    }

    private static HostingEnvironment hostNaming(Assembly assembly) => new() { ApplicationName = assembly.GetName().Name! };

    [Fact]
    public void the_host_name_replaces_a_guess()
    {
        var options = new WolverineOptions();

        // JasperFx guessed the entry assembly, which is what it does when that references a JasperFx tool like Marten
        options.ReadHostEnvironment(hostNaming(Module1Assembly),
            new JasperFxOptions { ApplicationAssembly = Assembly.GetEntryAssembly() });

        options.ApplicationAssembly.ShouldBe(Module1Assembly);
        options.Assemblies.ShouldContain(Module1Assembly);
    }

    [Fact]
    public void an_explicit_wolverine_application_assembly_wins()
    {
        var options = new WolverineOptions { ApplicationAssembly = ExplicitlyChosen };

        options.ReadHostEnvironment(hostNaming(Module1Assembly),
            new JasperFxOptions { ApplicationAssembly = Assembly.GetEntryAssembly() });

        options.ApplicationAssembly.ShouldBe(ExplicitlyChosen);
    }

    [Fact]
    public void an_explicit_jasperfx_application_assembly_wins()
    {
        var options = new WolverineOptions();

        // CritterStackDefaults(x => x.ApplicationAssembly = ...) or SetApplicationProject(...)
        var jasperfx = new JasperFxOptions { ApplicationAssembly = ExplicitlyChosen };
        options.ReadHostEnvironment(hostNaming(Module1Assembly), jasperfx);
        options.ReadJasperFxOptions(jasperfx);

        options.ApplicationAssembly.ShouldBe(ExplicitlyChosen);
    }

    [Fact]
    public void a_host_named_after_the_entry_assembly_changes_nothing()
    {
        var options = new WolverineOptions();

        options.ReadHostEnvironment(new HostingEnvironment { ApplicationName = Assembly.GetEntryAssembly()!.GetName().Name! },
            new JasperFxOptions { ApplicationAssembly = Assembly.GetEntryAssembly() });

        // Left for ReadJasperFxOptions to resolve, exactly as before
        options.ApplicationAssembly.ShouldBeNull();
    }

    [Fact]
    public void no_host_environment_changes_nothing()
    {
        var options = new WolverineOptions();

        options.ReadHostEnvironment(null, new JasperFxOptions { ApplicationAssembly = Assembly.GetEntryAssembly() });

        options.ApplicationAssembly.ShouldBeNull();
    }
}
