using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public record ForceRestartMessage(string Name);

public class ForceRestartMessageHandler
{
    public void Handle(ForceRestartMessage message)
    {
        // no-op; only used to prove the rebuilt listener still consumes
    }
}

// GH-3232: an operator/monitor must be able to force-recover a listener that reports Accepting but isn't actually
// consuming (a stuck transport channel the framework can't self-heal) without a process bounce. Bare StartAsync()
// is a no-op when Status == Accepting; RestartAsync(force: true) tears down and rebuilds regardless.
[Trait("Category", "Flaky")]
public class force_restart_listener_3232
{
    private static string nextQueue() => "force_restart_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task force_restart_rebuilds_an_accepting_listener_and_it_still_consumes()
    {
        var queue = nextQueue();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.ListenToRabbitQueue(queue);
            opts.PublishMessage<ForceRestartMessage>().ToRabbitQueue(queue);
        });

        var runtime = host.GetRuntime();
        var agent = (ListeningAgent)runtime.Endpoints.ActiveListeners()
            .Single(a => a.Uri.Scheme == "rabbitmq" && a.Uri.ToString().Contains(queue));

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        var before = agent.Listener;
        before.ShouldNotBeNull();

        // Gentle paths are a no-op while Accepting: the underlying IListener instance is unchanged.
        await agent.StartAsync();
        agent.Listener.ShouldBeSameAs(before);

        await agent.RestartAsync(force: false);
        agent.Listener.ShouldBeSameAs(before);

        // Force restart tears down and rebuilds even though Status still reports Accepting.
        await agent.RestartAsync(force: true);
        agent.Status.ShouldBe(ListeningStatus.Accepting);
        agent.Listener.ShouldNotBeNull();
        agent.Listener.ShouldNotBeSameAs(before);

        // And the rebuilt listener is genuinely live — a message sent after the restart is still handled.
        await host.TrackActivity().Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new ForceRestartMessage("Hakeem Olajuwon"));
    }

    [Fact]
    public async Task force_restart_recovers_a_stopped_listener()
    {
        var queue = nextQueue();
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.ListenToRabbitQueue(queue);
        });

        var runtime = host.GetRuntime();
        var agent = (ListeningAgent)runtime.Endpoints.ActiveListeners()
            .Single(a => a.Uri.Scheme == "rabbitmq" && a.Uri.ToString().Contains(queue));

        await agent.StopAndDrainAsync();
        agent.Status.ShouldBe(ListeningStatus.Stopped);

        await agent.RestartAsync(force: true);
        agent.Status.ShouldBe(ListeningStatus.Accepting);
        agent.Listener.ShouldNotBeNull();
    }
}
