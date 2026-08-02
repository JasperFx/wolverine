using System.Reflection;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3776: the application assembly is resolved by walking the stack for the first frame outside Wolverine
/// that isn't part of the framework. Under an async test fixture the frames between Wolverine and the test
/// class belong to the test RUNNER, so xUnit v3's own "xunit.v3.core" was being adopted for handler discovery.
/// Discovery then scanned an assembly containing none of the suite's handlers, and every InvokeAsync failed
/// with IndeterminateRoutesException — 43 of 86 tests in one CIPolecat shard, with the run's only visible
/// symptom being "Could not determine any valid subscribers or local handlers".
/// </summary>
public class application_assembly_is_never_a_test_runner
{
    [Theory]
    [InlineData("xunit.v3.core")]
    [InlineData("xunit.execution.dotnet")]
    [InlineData("xunit.runner.utility.netcoreapp10")]
    [InlineData("nunit.framework")]
    [InlineData("NUnit3.TestAdapter")]
    [InlineData("TUnit.Engine")]
    [InlineData("MSTest.TestAdapter")]
    [InlineData("testhost")]
    [InlineData("ReSharperTestRunner")]
    [InlineData("JetBrains.ReSharper.TestRunner.Merged")]
    public void recognizes_test_runner_assemblies(string assemblyName)
    {
        WolverineOptions.IsTestRunnerAssembly(assemblyName).ShouldBeTrue();
    }

    [Theory]
    [InlineData("CoreTests")]
    [InlineData("PolecatTests")]
    [InlineData("Wolverine")]
    [InlineData("Wolverine.SqlServer")]
    [InlineData("MyApp.Api")]
    // "Xenon" and "Nutrition" share a prefix boundary with xunit/nunit only if the check is sloppier
    // than StartsWith on the full runner token — pin that they are ordinary application assemblies.
    [InlineData("Xenon.Services")]
    [InlineData("Nutrition.Domain")]
    public void does_not_flag_ordinary_application_assemblies(string assemblyName)
    {
        WolverineOptions.IsTestRunnerAssembly(assemblyName).ShouldBeFalse();
    }

    [Fact]
    public void refuses_a_test_runner_assembly_handed_over_by_jasperfx()
    {
        var options = new WolverineOptions();

        // JasperFx does its own stack walk with no test-runner exclusion, so this is exactly what it handed
        // Wolverine on the failing CI runs.
        var xunitCore = typeof(FactAttribute).Assembly;
        WolverineOptions.IsTestRunnerAssembly(xunitCore.GetName().Name!).ShouldBeTrue();

        options.ReadJasperFxOptions(new JasperFxOptions { ApplicationAssembly = xunitCore });

        options.ApplicationAssembly.ShouldNotBeNull();
        WolverineOptions.IsTestRunnerAssembly(options.ApplicationAssembly!.GetName().Name!).ShouldBeFalse();
    }

    [Fact]
    public async Task a_host_bootstrapped_from_a_test_assembly_never_adopts_the_runner()
    {
        // The end-to-end guard, and the one that would have caught GH-3776: whatever the stack happens to
        // look like on this platform and runner, the assembly Wolverine scans for handlers must be the test
        // assembly that registered the host — never the runner hosting it.
        using var host = await Host.CreateDefaultBuilder().UseWolverine()
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var adopted = options.ApplicationAssembly.ShouldNotBeNull();

        WolverineOptions.IsTestRunnerAssembly(adopted.GetName().Name!)
            .ShouldBeFalse($"handler discovery adopted the test runner assembly '{adopted.GetName().Name}'");

        options.Assemblies.ShouldContain(typeof(application_assembly_is_never_a_test_runner).Assembly);
    }
}
