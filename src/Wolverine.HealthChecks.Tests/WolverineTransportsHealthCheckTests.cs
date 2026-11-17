using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.HealthChecks.Tests;

public class WolverineTransportsHealthCheckTests
{
    private static HealthCheckContext ContextFor(HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("wolverine-transports", Substitute.For<IHealthCheck>(),
                failureStatus, tags: null)
        };
    }

    private static IWolverineRuntime BuildRuntime(bool started, params (string protocol, TransportHealthStatus? status)[] transports)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        var options = new WolverineOptions();
        runtime.Options.Returns(options);

        if (!started)
        {
            runtime.When(r => r.AssertHasStarted()).Do(_ => throw new WolverineHasNotStartedException());
        }

        foreach (var (protocol, status) in transports)
        {
            var transport = Substitute.For<ITransport>();
            transport.Protocol.Returns(protocol);
            transport.BuildHealthCheck(runtime)
                .Returns(status.HasValue ? new FakeTransportHealthCheck(protocol, status.Value) : null);
            options.Transports.Add(transport);
        }

        return runtime;
    }

    [Fact]
    public async Task fails_before_the_runtime_has_started()
    {
        var check = new WolverineTransportsHealthCheck(BuildRuntime(false, ("fake", TransportHealthStatus.Healthy)));

        var result = await check.CheckHealthAsync(ContextFor(HealthStatus.Degraded), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task healthy_when_no_transport_reports_its_health()
    {
        var check = new WolverineTransportsHealthCheck(BuildRuntime(true, ("silent", null)));

        var result = await check.CheckHealthAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task healthy_when_every_transport_is_healthy()
    {
        var check = new WolverineTransportsHealthCheck(BuildRuntime(true,
            ("one", TransportHealthStatus.Healthy), ("two", TransportHealthStatus.Healthy)));

        var result = await check.CheckHealthAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["one:one"].ShouldBe("Healthy");
        result.Data["two:two"].ShouldBe("Healthy");
    }

    [Fact]
    public async Task degraded_when_one_transport_is_degraded()
    {
        var check = new WolverineTransportsHealthCheck(BuildRuntime(true,
            ("one", TransportHealthStatus.Healthy), ("two", TransportHealthStatus.Degraded)));

        var result = await check.CheckHealthAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("two:two is Degraded");
    }

    [Fact]
    public async Task the_worst_status_wins_and_uses_the_registration_failure_status()
    {
        var check = new WolverineTransportsHealthCheck(BuildRuntime(true,
            ("one", TransportHealthStatus.Unhealthy), ("two", TransportHealthStatus.Degraded)));

        var result = await check.CheckHealthAsync(ContextFor(HealthStatus.Degraded), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description!.ShouldContain("one:one is Unhealthy");
    }

    [Fact]
    public async Task a_transport_check_that_throws_counts_as_unhealthy()
    {
        var runtime = BuildRuntime(true);
        var transport = Substitute.For<ITransport>();
        transport.Protocol.Returns("boom");
        transport.BuildHealthCheck(runtime).Returns(new ThrowingTransportHealthCheck());
        runtime.Options.Transports.Add(transport);

        var result = await new WolverineTransportsHealthCheck(runtime)
            .CheckHealthAsync(ContextFor(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldContain("broker is gone");
    }

    private class FakeTransportHealthCheck(string name, TransportHealthStatus status) : WolverineTransportHealthCheck
    {
        public override string TransportName => name;
        public override string Protocol => name;

        public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TransportHealthResult(name, name, status, $"{name} says {status}",
                DateTimeOffset.UtcNow, new Dictionary<string, object>()));
        }
    }

    private class ThrowingTransportHealthCheck : WolverineTransportHealthCheck
    {
        public override string TransportName => "boom";
        public override string Protocol => "boom";

        public override Task<TransportHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("broker is gone");
        }
    }
}
