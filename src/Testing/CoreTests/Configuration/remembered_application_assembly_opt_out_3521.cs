using System.Reflection;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Module1;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3521. The first host to start in a process remembers its application assembly, and every later host that names
/// none reuses it. <see cref="WolverineOptions.RememberApplicationAssembly"/> = false opts a host out of that, and
/// <see cref="WolverineOptions.ResetRememberedApplicationAssembly"/> clears it, both the same way JasperFx's own
/// switches do.
/// </summary>
public class remembered_application_assembly_opt_out_3521
{
    private static readonly Assembly ThisTestAssembly = typeof(remembered_application_assembly_opt_out_3521).Assembly;
    private static readonly Assembly Module1Assembly = typeof(CompositionRootInAClassLibrary).Assembly;

    [Fact]
    public async Task a_host_that_opts_out_uses_its_own_assembly_after_another_host_pinned_one()
    {
        using var pin = ProcessWideApplicationAssemblyPin.AsFirstHost();

        // The first host in the process registers Wolverine from Module1, so Module1 is remembered
        using (var first = CompositionRootInAClassLibrary.BuildHost())
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            first.Services.GetRequiredService<WolverineOptions>().ApplicationAssembly.ShouldBe(Module1Assembly);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        JasperFxOptions.RememberedApplicationAssembly.ShouldBe(Module1Assembly);

        #region sample_opting_out_of_the_remembered_application_assembly

        using var second = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Don't reuse the application assembly an earlier host in this process
                // remembered, and don't remember this one for later hosts either
                opts.RememberApplicationAssembly = false;
            }).StartAsync(TestContext.Current.CancellationToken);

        #endregion

        var options = second.Services.GetRequiredService<WolverineOptions>();
        options.ApplicationAssembly.ShouldBe(ThisTestAssembly);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();

        second.Services.GetRequiredService<JasperFxOptions>().RememberApplicationAssembly.ShouldBeFalse();

        // The first host's pin is left alone for anyone else
        JasperFxOptions.RememberedApplicationAssembly.ShouldBe(Module1Assembly);

        await second.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_first_host_that_opts_out_remembers_nothing()
    {
        using var pin = ProcessWideApplicationAssemblyPin.AsFirstHost();

        using var host = CompositionRootInAClassLibrary.BuildHost(opts => opts.RememberApplicationAssembly = false);
        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<WolverineOptions>().ApplicationAssembly.ShouldBe(Module1Assembly);

        JasperFxOptions.RememberedApplicationAssembly.ShouldBeNull();
        WolverineOptions.RememberedApplicationAssembly.ShouldBeNull();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task resetting_lets_the_next_host_resolve_its_own_assembly()
    {
        using var pin = ProcessWideApplicationAssemblyPin.AsFirstHost();

        using (var first = CompositionRootInAClassLibrary.BuildHost())
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        JasperFxOptions.RememberedApplicationAssembly.ShouldBe(Module1Assembly);

        #region sample_resetting_the_remembered_application_assembly

        // In a test fixture that starts several hosts in one process: forget the
        // application assembly an earlier host remembered
        WolverineOptions.ResetRememberedApplicationAssembly();

        #endregion

        JasperFxOptions.RememberedApplicationAssembly.ShouldBeNull();
        WolverineOptions.RememberedApplicationAssembly.ShouldBeNull();

        using var second = await Host.CreateDefaultBuilder().UseWolverine()
            .StartAsync(TestContext.Current.CancellationToken);

        var options = second.Services.GetRequiredService<WolverineOptions>();
        options.ApplicationAssembly.ShouldBe(ThisTestAssembly);
        options.ApplicationAssemblyReuseWarning.ShouldBeNull();

        await second.StopAsync(TestContext.Current.CancellationToken);
    }
}
