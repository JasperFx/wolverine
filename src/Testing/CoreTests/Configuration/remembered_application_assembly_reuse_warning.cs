using System.Reflection;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3521: the application assembly used for handler discovery is a process-wide value pinned by whichever
/// Wolverine host started FIRST in the process (JasperFxOptions.ApplicationAssembly / the RememberedApplicationAssembly
/// static). In a test process that stands up multiple hosts across different assemblies, a later host silently
/// inherits the first host's assembly, so its conventional handlers vanish with only a downstream "No routes can
/// be determined" as a symptom. These tests pin the loud warning that now surfaces that divergence.
/// </summary>
public class remembered_application_assembly_reuse_warning
{
    private static readonly Assembly WolverineAssembly = typeof(WolverineOptions).Assembly;
    private static readonly Assembly ThisTestAssembly = typeof(remembered_application_assembly_reuse_warning).Assembly;

    private static JasperFxOptions JasperFxWithApplicationAssembly(Assembly assembly)
    {
        return new JasperFxOptions { ApplicationAssembly = assembly };
    }

    [Fact]
    public async Task a_normal_single_assembly_host_does_not_warn()
    {
        // Sanity + false-positive guard: a normal host registered from this test assembly resolves the same
        // application assembly it adopts, so it must NOT warn. Also pins that the constructor captured the
        // caller's assembly (this test assembly), not "Wolverine".
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync();
        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.RegistrationCallingAssembly!.GetName().Name.ShouldBe(ThisTestAssembly.GetName().Name);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();
    }

    [Fact]
    public void warns_when_the_adopted_assembly_diverges_from_where_the_host_registered()
    {
        var options = new WolverineOptions();
        var registered = options.RegistrationCallingAssembly;
        registered.ShouldNotBeNull();

        // Simulate the process-pinned jasperfx assembly being a DIFFERENT one than where this host registered.
        var pinnedElsewhere = registered!.GetName().Name == WolverineAssembly.GetName().Name
            ? ThisTestAssembly
            : WolverineAssembly;

        options.ReadJasperFxOptions(JasperFxWithApplicationAssembly(pinnedElsewhere));

        // The pinned value is still adopted...
        options.ApplicationAssembly!.GetName().Name.ShouldBe(pinnedElsewhere.GetName().Name);

        // ...but the divergence is now loud, naming both the adopted and the skipped assembly.
        options.ApplicationAssemblyReuseWarning.ShouldNotBeNull();
        options.ApplicationAssemblyReuseWarning.ShouldContain(registered.GetName().Name!);
        options.ApplicationAssemblyReuseWarning.ShouldContain(pinnedElsewhere.GetName().Name!);
    }

    [Fact]
    public void does_not_warn_when_the_adopted_assembly_matches_where_the_host_registered()
    {
        var options = new WolverineOptions();
        var registered = options.RegistrationCallingAssembly;
        registered.ShouldNotBeNull();

        options.ReadJasperFxOptions(JasperFxWithApplicationAssembly(registered!));

        options.ApplicationAssemblyReuseWarning.ShouldBeNull();
    }

    [Fact]
    public void does_not_warn_when_the_user_set_the_application_assembly_explicitly()
    {
        var options = new WolverineOptions();

        // An explicit choice is always honored silently, regardless of what the process pinned.
        options.ApplicationAssembly = WolverineAssembly;
        options.ReadJasperFxOptions(JasperFxWithApplicationAssembly(ThisTestAssembly));

        options.ApplicationAssemblyReuseWarning.ShouldBeNull();
    }
}
