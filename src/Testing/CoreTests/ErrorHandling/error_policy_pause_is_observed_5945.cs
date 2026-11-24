using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.ErrorHandling;

/// <summary>
/// GH-5945. <c>OnException&lt;T&gt;().Requeue().AndPauseProcessing(TimeSpan)</c> paused the listener through
/// <see cref="PauseListenerContinuation" /> and told nothing but the failing message's own trace. A monitoring tool
/// saw a listener stop consuming but could not say that an error policy did it, for how long, or because of which
/// exception, and nothing reported the automatic resume either.
/// </summary>
public class error_policy_pause_is_observed_5945 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;
    private IWolverineObserver _originalObserver = null!;
    private readonly PauseObserver theObserver = new();

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.LocalQueue("durable-pause-5945").UseDurableInbox();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
        _originalObserver = theRuntime.Observer;
        theRuntime.Observer = theObserver;
    }

    public async ValueTask DisposeAsync()
    {
        theRuntime.Observer = _originalObserver;
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<IListenerCircuit> startedListenerFor(string name)
    {
        var endpoint = new StubEndpoint(name, new StubTransport()) { Mode = EndpointMode.BufferedInMemory };
        await theRuntime.Endpoints.StartListenerAsync(endpoint, TestContext.Current.CancellationToken);

        var agent = theRuntime.Endpoints.FindListeningAgent(endpoint.Uri)!;
        agent.Status.ShouldBe(ListeningStatus.Accepting);
        return agent;
    }

    // What the executor does when the AndPauseProcessing rule matches: build the continuation for the exception,
    // then execute it for the failing envelope
    private async Task pauseByErrorPolicyAsync(Uri address, TimeSpan pauseTime, Exception exception)
    {
        var envelope = new Envelope { Destination = address };
        var lifecycle = Substitute.For<IEnvelopeLifecycle>();
        lifecycle.Envelope.Returns(envelope);

        var continuation = new PauseListenerContinuation(pauseTime).Build(exception, envelope);
        await continuation.ExecuteAsync(lifecycle, theRuntime, DateTimeOffset.UtcNow, null);
    }

    [Fact]
    public async Task the_pause_and_the_timed_resume_are_both_observed()
    {
        var agent = await startedListenerFor("timed-5945");
        var exception = new TimeoutException("the downstream service is slow");

        await pauseByErrorPolicyAsync(agent.Endpoint.Uri, 250.Milliseconds(), exception);
        agent.Status.ShouldBe(ListeningStatus.Paused);

        var paused = theObserver.Paused.Single();
        paused.Endpoint.ShouldBeSameAs(agent.Endpoint);
        paused.PauseTime.ShouldBe(250.Milliseconds());
        paused.Exception.ShouldBeSameAs(exception);

        theObserver.Resumed.ShouldBeEmpty();

        await waitUntil(() => agent.Status == ListeningStatus.Accepting);
        await waitUntil(() => theObserver.Resumed.Count == 1);
        theObserver.Resumed.Single().ShouldBeSameAs(agent.Endpoint);

        // Once per pause
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);
        theObserver.Resumed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task an_explicit_start_before_the_pause_ends_is_the_resume()
    {
        var agent = await startedListenerFor("explicit-5945");

        await pauseByErrorPolicyAsync(agent.Endpoint.Uri, 1.Hours(), new InvalidOperationException());
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        theObserver.Resumed.Single().ShouldBeSameAs(agent.Endpoint);

        // A later restart of a healthy listener is not another resume
        await agent.RestartAsync(force: true);
        theObserver.Resumed.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_pause_no_error_policy_made_is_not_reported()
    {
        var agent = await startedListenerFor("operator-5945");

        await agent.PauseAsync(250.Milliseconds());
        await waitUntil(() => agent.Status == ListeningStatus.Accepting);
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        theObserver.Paused.ShouldBeEmpty();
        theObserver.Resumed.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_pause_that_supersedes_the_error_policys_takes_the_resume_with_it()
    {
        var agent = await startedListenerFor("superseded-5945");

        await pauseByErrorPolicyAsync(agent.Endpoint.Uri, 1.Hours(), new InvalidOperationException());

        // An operator's pause replaces the error policy's: the start that ends it is the operator's
        await agent.PauseAsync(1.Hours());
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        theObserver.Paused.Count.ShouldBe(1);
        theObserver.Resumed.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_observer_that_throws_does_not_stop_the_pause_or_the_resume()
    {
        theObserver.Throw = true;
        var agent = await startedListenerFor("throwing-5945");

        await pauseByErrorPolicyAsync(agent.Endpoint.Uri, 250.Milliseconds(), new InvalidOperationException());
        agent.Status.ShouldBe(ListeningStatus.Paused);

        await waitUntil(() => agent.Status == ListeningStatus.Accepting);
        await waitUntil(() => theObserver.Resumed.Count == 1);
    }

    [Fact]
    public async Task a_durable_local_queue_reports_the_pause_and_the_resume_too()
    {
        var address = new Uri("local://durable-pause-5945");
        var queue = (IListenerCircuit)theRuntime.Endpoints.AgentForLocalQueue(address)!;
        var exception = new InvalidOperationException("boom");

        await pauseByErrorPolicyAsync(address, 250.Milliseconds(), exception);
        queue.Status.ShouldBe(ListeningStatus.Paused);

        theObserver.Paused.Single().Exception.ShouldBeSameAs(exception);
        theObserver.Paused.Single().Endpoint.ShouldBeSameAs(queue.Endpoint);

        await waitUntil(() => queue.Status == ListeningStatus.Accepting);
        await waitUntil(() => theObserver.Resumed.Count == 1);
        theObserver.Resumed.Single().ShouldBeSameAs(queue.Endpoint);
    }

    private static async Task waitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}

/// <summary>An observer that implements the GH-5945 members on top of everything an observer has to.</summary>
internal class PauseObserver : LegacyBreakerObserver, IWolverineObserver
{
    public ConcurrentQueue<(Endpoint Endpoint, TimeSpan PauseTime, Exception? Exception)> Paused { get; } = new();
    public ConcurrentQueue<Endpoint> Resumed { get; } = new();

    public bool Throw { get; set; }

    public Task ListenerPaused(Endpoint endpoint, TimeSpan pauseTime, Exception? exception)
    {
        Paused.Enqueue((endpoint, pauseTime, exception));
        return Throw ? throw new InvalidOperationException("the alert service is down") : Task.CompletedTask;
    }

    public Task ListenerResumed(Endpoint endpoint)
    {
        Resumed.Enqueue(endpoint);
        return Throw ? throw new InvalidOperationException("the alert service is down") : Task.CompletedTask;
    }
}
