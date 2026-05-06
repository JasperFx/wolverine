using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.HealthChecks;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.HealthChecks.Tests;

public class WolverineListenerHealthCheckTests
{
    private static HealthCheckContext ContextFor(string name = "wolverine-listeners", HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        return new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(name, Substitute.For<IHealthCheck>(), failureStatus, tags: null)
        };
    }

    private static IListeningAgent FakeListener(ListeningStatus status, string uri)
    {
        var listener = Substitute.For<IListeningAgent>();
        listener.Status.Returns(status);
        listener.Uri.Returns(new Uri(uri));
        return listener;
    }

    private static IWolverineRuntime RuntimeWithListeners(params IListeningAgent[] listeners)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        var endpoints = Substitute.For<IEndpointCollection>();
        endpoints.ActiveListeners().Returns(listeners);
        runtime.Endpoints.Returns(endpoints);
        return runtime;
    }

    [Fact]
    public async Task healthy_when_all_listeners_accepting()
    {
        var runtime = RuntimeWithListeners(
            FakeListener(ListeningStatus.Accepting, "rabbitmq://orders"),
            FakeListener(ListeningStatus.Accepting, "local://default")
        );

        var result = await new WolverineListenerHealthCheck(runtime).CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["accepting"].ShouldBe(2);
        result.Data["listenerCount"].ShouldBe(2);
        var perListener = (Dictionary<string, object>)result.Data["listeners"]!;
        perListener["rabbitmq://orders/"].ShouldBe("Accepting");
    }

    [Fact]
    public async Task degraded_when_any_listener_too_busy()
    {
        var runtime = RuntimeWithListeners(
            FakeListener(ListeningStatus.Accepting, "local://default"),
            FakeListener(ListeningStatus.TooBusy, "rabbitmq://orders")
        );

        var result = await new WolverineListenerHealthCheck(runtime).CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Data["tooBusy"].ShouldBe(1);
    }

    [Fact]
    public async Task degraded_when_any_listener_globally_latched()
    {
        var runtime = RuntimeWithListeners(
            FakeListener(ListeningStatus.Accepting, "local://default"),
            FakeListener(ListeningStatus.GloballyLatched, "rabbitmq://orders")
        );

        var result = await new WolverineListenerHealthCheck(runtime).CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Data["globallyLatched"].ShouldBe(1);
    }

    [Fact]
    public async Task unhealthy_when_all_listeners_stopped()
    {
        var runtime = RuntimeWithListeners(
            FakeListener(ListeningStatus.Stopped, "rabbitmq://orders"),
            FakeListener(ListeningStatus.Stopped, "local://default")
        );

        var result = await new WolverineListenerHealthCheck(runtime).CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data["stopped"].ShouldBe(2);
    }

    [Fact]
    public async Task healthy_with_note_when_no_listeners_match()
    {
        // No listeners at all → reported healthy with explanatory note. Operators
        // who want a missing listener to fail can register a separate check.
        var runtime = RuntimeWithListeners();

        var result = await new WolverineListenerHealthCheck(runtime).CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data["listenerCount"].ShouldBe(0);
    }

    [Fact]
    public async Task filter_scopes_listeners()
    {
        var runtime = RuntimeWithListeners(
            FakeListener(ListeningStatus.Stopped, "rabbitmq://orders"),
            FakeListener(ListeningStatus.Accepting, "local://default")
        );

        // Only inspect the rabbit listener — should be Unhealthy because the
        // filter narrows the population to one stopped listener.
        var check = new WolverineListenerHealthCheck(runtime,
            agent => agent.Uri.Scheme == "rabbitmq");

        var result = await check.CheckHealthAsync(ContextFor());

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data["listenerCount"].ShouldBe(1);
        result.Data["stopped"].ShouldBe(1);
    }

    [Fact]
    public async Task uses_failure_status_from_registration()
    {
        var runtime = RuntimeWithListeners(
            FakeListener(ListeningStatus.Stopped, "rabbitmq://orders")
        );

        var result = await new WolverineListenerHealthCheck(runtime)
            .CheckHealthAsync(ContextFor(failureStatus: HealthStatus.Degraded));

        result.Status.ShouldBe(HealthStatus.Degraded);
    }
}
