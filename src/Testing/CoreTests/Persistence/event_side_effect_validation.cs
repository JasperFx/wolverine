using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence;

// Storage.AppendEvents() / Storage.StartStream() need an event store. Without one the failure used to be a
// raw codegen "cannot determine how to build variable of type IEventOperations" deep in startup, which says
// nothing about the actual mistake. These pin the helpful message instead.
public class event_side_effect_validation
{
    [Fact]
    public async Task helpful_error_when_appending_with_no_event_store_registered()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NoStoreAppendHandler));
                }).StartAsync();
        });

        ex.Message.ShouldContain("no registered event store");
        ex.Message.ShouldContain("IntegrateWithWolverine()");
        ex.Message.ShouldContain(nameof(AppendEvents));
    }

    [Fact]
    public async Task helpful_error_when_starting_a_stream_with_no_event_store_registered()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NoStoreStartHandler));
                }).StartAsync();
        });

        ex.Message.ShouldContain("no registered event store");
        ex.Message.ShouldContain(nameof(StartStream));
    }
}

// These deliberately break bootstrapping, so they must never be found by the conventional discovery
// that every other CoreTests host runs -- otherwise this file fails 500+ unrelated tests.
public record AppendSomething(Guid Id);

public record StartSomething(Guid Id);

public record SomethingHappened;

[WolverineIgnore]
public static class NoStoreAppendHandler
{
    public static AppendEvents Handle(AppendSomething command)
        => Storage.AppendEvents(command.Id, new SomethingHappened());
}

[WolverineIgnore]
public static class NoStoreStartHandler
{
    public static StartStream Handle(StartSomething command)
        => Storage.StartStream(command.Id, new SomethingHappened());
}
