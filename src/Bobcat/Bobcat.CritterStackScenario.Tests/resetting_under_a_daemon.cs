using JasperFx.Events;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#680: the reset pauses every projection coordinator around the delete and resumes it afterwards, as
// Marten's ResetAllMartenDataAsync does, so an async daemon never sees its store emptied under it. Against fakes
// registered the way Marten and Fisher register theirs; the real-daemon half is resetting_a_scenario.

public class resetting_under_a_daemon
{
    private sealed class LoggingStore(List<string> log, string name, bool fails = false) : FakeEventStore(name)
    {
        public Ops Advanced { get; } = new(log, name, fails);

        public sealed class Ops(List<string> log, string name, bool fails)
        {
            public Task ResetAllDataAsync(CancellationToken token)
            {
                log.Add($"reset {name}");
                return fails ? Task.FromException(new InvalidOperationException("the database went away")) : Task.CompletedTask;
            }
        }
    }

    private static IProjectionCoordinator coordinator(List<string> log, string name)
    {
        var coordinator = Substitute.For<IProjectionCoordinator>();
        coordinator.PauseAsync().Returns(_ => { log.Add($"pause {name}"); return Task.CompletedTask; });
        coordinator.ResumeAsync().Returns(_ => { log.Add($"resume {name}"); return Task.CompletedTask; });
        return coordinator;
    }

    [Fact]
    public async Task every_coordinator_is_paused_before_any_store_is_reset_and_resumed_after()
    {
        var log = new List<string>();
        var main = coordinator(log, "main");
        var ancillary = coordinator(log, "ancillary");

        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(new LoggingStore(log, "main"));
        services.AddSingleton<IEventStore>(new LoggingStore(log, "other"));
        // The main store's coordinator under the JasperFx interface and as a hosted service (Marten, Fisher)
        services.AddSingleton(main);
        services.AddSingleton<IHostedService>(s => s.GetRequiredService<IProjectionCoordinator>());
        // An ancillary store's only as a hosted service (Marten's AddMartenStore<T>, Fisher's AddFisherStore<T>)
        services.AddSingleton<IHostedService>(ancillary);

        var provider = services.BuildServiceProvider();
        provider.ProjectionCoordinators().ShouldBe([main, ancillary]);

        await provider.ResetEventStoresAsync();

        log.ShouldBe(["pause main", "pause ancillary", "reset main", "reset other", "resume main", "resume ancillary"]);
    }

    [Fact]
    public async Task the_coordinators_are_resumed_when_a_reset_throws()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(new LoggingStore(log, "main", fails: true));
        services.AddSingleton(coordinator(log, "main"));

        var provider = services.BuildServiceProvider();

        await Should.ThrowAsync<InvalidOperationException>(() => provider.ResetEventStoresAsync());

        log.ShouldBe(["pause main", "reset main", "resume main"]);
    }

    [Fact]
    public async Task a_host_with_no_daemon_resets_as_before()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IEventStore>(new LoggingStore(log, "main"));

        var provider = services.BuildServiceProvider();
        provider.ProjectionCoordinators().ShouldBeEmpty();

        await provider.ResetEventStoresAsync();

        log.ShouldBe(["reset main"]);
    }
}
