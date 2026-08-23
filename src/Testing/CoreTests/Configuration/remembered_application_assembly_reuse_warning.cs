using System.Reflection;
using JasperFx;
using Module1;
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
        using var host = await Host.CreateDefaultBuilder().UseWolverine().StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.RegistrationCallingAssembly!.GetName().Name.ShouldBe(ThisTestAssembly.GetName().Name);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();
    }

    [Fact]
    public async Task captures_the_assembly_that_called_UseWolverine_not_the_one_that_called_Build()
    {
        // GH-3778. IHostBuilder.UseWolverine() defers into a ConfigureServices callback that runs during
        // Build(), so re-deriving the caller from the stack inside WolverineOptions' constructor learns
        // who called BUILD, not who registered Wolverine. Any harness whose hosts are built by a shared
        // helper in another assembly — which is most of them — therefore resolved to that helper, and the
        // divergence warning it feeds fired on healthy hosts: instrumenting a fully green CIPolecat shard
        // found RegistrationCallingAssembly resolving to Wolverine.SqlServer on 81 of 82 hosts.
        var builder = Host.CreateDefaultBuilder().UseWolverine();

        // Registered above, in THIS assembly. Built below, in Module1.
        using var host = HostBuiltFromAnotherAssembly.Build(builder);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();

        options.RegistrationCallingAssembly!.GetName().Name.ShouldBe(ThisTestAssembly.GetName().Name);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
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

    /// <summary>
    /// GH-3778. Routes through the GH-3776 branch — a JasperFx-pinned assembly that is a TEST RUNNER is
    /// dropped — so ApplicationAssembly is null by the time establishApplicationAssembly(null) runs, and
    /// the RememberedApplicationAssembly branch is the one that decides. That is the branch that used to
    /// adopt a process-wide pin in silence.
    /// </summary>
    private static JasperFxOptions JasperFxPinnedToTheTestRunner()
    {
        return new JasperFxOptions { ApplicationAssembly = typeof(FactAttribute).Assembly };
    }

    [Fact]
    public void warns_when_the_remembered_assembly_diverges_from_where_the_host_registered()
    {
        var previous = WolverineOptions.RememberedApplicationAssembly;

        try
        {
            var options = new WolverineOptions();
            var registered = options.RegistrationCallingAssembly;
            registered.ShouldNotBeNull();

            // What a FIRST host registered from another assembly leaves behind for every later host.
            WolverineOptions.RememberedApplicationAssembly = WolverineAssembly;
            registered!.GetName().Name.ShouldNotBe(WolverineAssembly.GetName().Name);

            options.ReadJasperFxOptions(JasperFxPinnedToTheTestRunner());

            // The pin is still adopted -- this fix makes it audible, it does not change what wins.
            options.ApplicationAssembly!.GetName().Name.ShouldBe(WolverineAssembly.GetName().Name);

            options.ApplicationAssemblyReuseWarning.ShouldNotBeNull();
            options.ApplicationAssemblyReuseWarning.ShouldContain(registered.GetName().Name!);
            options.ApplicationAssemblyReuseWarning.ShouldContain(WolverineAssembly.GetName().Name!);
        }
        finally
        {
            // Process-wide static: leaving it set would pin handler discovery for every later test.
            WolverineOptions.RememberedApplicationAssembly = previous;
        }
    }

    [Fact]
    public void does_not_warn_when_the_remembered_assembly_matches_where_the_host_registered()
    {
        var previous = WolverineOptions.RememberedApplicationAssembly;

        try
        {
            var options = new WolverineOptions();
            options.RegistrationCallingAssembly.ShouldNotBeNull();

            WolverineOptions.RememberedApplicationAssembly = options.RegistrationCallingAssembly;

            options.ReadJasperFxOptions(JasperFxPinnedToTheTestRunner());

            options.ApplicationAssemblyReuseWarning.ShouldBeNull();
        }
        finally
        {
            WolverineOptions.RememberedApplicationAssembly = previous;
        }
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
