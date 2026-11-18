using Alba;
using ApplicationAssemblyWebApp;
using IntegrationTests;
using JasperFx;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ApplicationProgram = ApplicationAssemblyWebApp.Program;

namespace Wolverine.Http.Tests;

// GH-5667. Under Alba (WebApplicationFactory) the ENTRY assembly is the test project, not the application. An
// application that registers Marten before UseWolverine() had JasperFx pin that entry assembly as the process-wide
// application assembly -- JasperFx short-circuits to the entry assembly whenever it references a JasperFx tool such
// as Marten, which a test project usually does -- and Wolverine adopted the pin. Handler discovery then scanned the
// test project, found nothing, and every message failed with IndeterminateRoutesException. A pin left by an earlier
// host in the same process (WolverineWebApi, in this project) did the same thing.
//
// ApplicationAssemblyWebApp must never set ApplicationAssembly itself; the point is that nothing names it except the
// host's IHostEnvironment.ApplicationName, which WebApplicationFactory sets to the application's assembly.
public class application_assembly_under_web_application_factory
{
    private static async Task<IAlbaHost> hostTheApplication(Action<IServiceCollection>? configure = null)
    {
        return await AlbaHost.For<ApplicationProgram>(x =>
        {
            x.UseSetting("ConnectionStrings:marten", Servers.PostgresConnectionString);
            x.ConfigureServices(services =>
            {
                services.DisableAllExternalWolverineTransports();
                configure?.Invoke(services);
            });
        });
    }

    // The reported case: this is the first host in the process, so AddMarten() pins the test project. The pins are
    // process-wide, so they are put back afterwards for the rest of the suite (which runs one test at a time)
    private static async Task asTheFirstHostInTheProcess(Func<Task> test)
    {
        var jasperFxPin = JasperFxOptions.RememberedApplicationAssembly;
        var wolverinePin = WolverineOptions.RememberedApplicationAssembly;
        WolverineOptions.ResetRememberedApplicationAssembly();

        try
        {
            await test();
        }
        finally
        {
            JasperFxOptions.RememberedApplicationAssembly = jasperFxPin;
            WolverineOptions.RememberedApplicationAssembly = wolverinePin;
        }
    }

    private static async Task theHostedApplicationIsScanned()
    {
        await using var host = await hostTheApplication();

        host.Services.GetRequiredService<WolverineOptions>().ApplicationAssembly
            .ShouldBe(typeof(ApplicationProgram).Assembly);

        var pong = await host.MessageBus().InvokeAsync<PongApplication>(new PingApplication("Alba"),
            TestContext.Current.CancellationToken);

        pong.Name.ShouldBe("Alba");
    }

    [Fact]
    public Task the_first_host_in_the_process_scans_the_hosted_application_not_the_test_project()
        => asTheFirstHostInTheProcess(theHostedApplicationIsScanned);

    [Fact]
    public Task a_later_host_scans_the_hosted_application_not_the_assembly_an_earlier_host_pinned()
        => theHostedApplicationIsScanned();

    [Fact]
    public Task an_explicit_application_assembly_still_wins()
    {
        return asTheFirstHostInTheProcess(async () =>
        {
            // The test project holds no handler for PingApplication, so this would be a poor choice in real life.
            // It only proves that an explicit choice is honored over the name the host gives
            var explicitChoice = typeof(application_assembly_under_web_application_factory).Assembly;

            await using var host = await hostTheApplication(services =>
                services.ConfigureWolverine(opts => opts.ApplicationAssembly = explicitChoice));

            host.Services.GetRequiredService<WolverineOptions>().ApplicationAssembly.ShouldBe(explicitChoice);
        });
    }

    // Keeps Marten referenced from this assembly's own IL, which is the condition that made JasperFx choose the
    // entry assembly. Wolverine.Http.Tests references Marten in plenty of other places; this test does not rely on them
    private static readonly Type _martenIsReferenced = typeof(IDocumentStore);
}
