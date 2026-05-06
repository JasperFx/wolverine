using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.HealthChecks;
using Wolverine.Runtime;

namespace Wolverine.HealthChecks.Tests;

public class WolverineBusHealthCheckTests
{
    private static HealthCheckContext ContextFor(string name = "wolverine", HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(name, Substitute.For<IHealthCheck>(), failureStatus, tags: null)
        };
    }

    private static IWolverineRuntime BuildRuntime(bool started, bool cancellationRequested)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions { ServiceName = "test-service" });
        var cts = new CancellationTokenSource();
        if (cancellationRequested) cts.Cancel();
        runtime.Cancellation.Returns(cts.Token);

        if (!started)
        {
            runtime.When(r => r.AssertHasStarted()).Do(_ => throw new WolverineHasNotStartedException());
        }

        runtime.Endpoints.Returns(Substitute.For<IEndpointCollection>());
        return runtime;
    }

    [Fact]
    public async Task healthy_when_started_and_not_cancelling()
    {
        var runtime = BuildRuntime(started: true, cancellationRequested: false);
        var check = new WolverineBusHealthCheck(runtime);

        var result = await check.CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data.ShouldContainKey("started");
        result.Data["started"].ShouldBe(true);
        result.Data["cancellationRequested"].ShouldBe(false);
        result.Data["serviceName"].ShouldBe("test-service");
    }

    [Fact]
    public async Task unhealthy_when_not_yet_started()
    {
        var runtime = BuildRuntime(started: false, cancellationRequested: false);
        var check = new WolverineBusHealthCheck(runtime);

        var result = await check.CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data["started"].ShouldBe(false);
        result.Exception.ShouldBeOfType<WolverineHasNotStartedException>();
    }

    [Fact]
    public async Task uses_failure_status_from_registration()
    {
        var runtime = BuildRuntime(started: false, cancellationRequested: false);
        var check = new WolverineBusHealthCheck(runtime);

        var result = await check.CheckHealthAsync(ContextFor(failureStatus: HealthStatus.Degraded));

        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task unhealthy_when_runtime_cancellation_requested()
    {
        var runtime = BuildRuntime(started: true, cancellationRequested: true);
        var check = new WolverineBusHealthCheck(runtime);

        var result = await check.CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data["cancellationRequested"].ShouldBe(true);
    }
}
