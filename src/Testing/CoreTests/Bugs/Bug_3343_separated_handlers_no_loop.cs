using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Bugs;

// GH-3343: reporter claims MultipleHandlerBehavior.Separated with two handlers for one event type causes
// an OOM / pod crash — the first handler runs, the second never appears. This exercises the reporter's
// shape (two handler classes for the same message, each with a ctor-injected scoped service + logger, the
// same policies) and asserts a BOUNDED, single execution of each handler with no runaway fan-out loop.
// An infinite fan-out loop would surface here as a tracked-session timeout or an execution count > 2.
public class Bug_3343_separated_handlers_no_loop
{
    private readonly ITestOutputHelper _output;

    public Bug_3343_separated_handlers_no_loop(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task both_separated_handlers_run_exactly_once_no_loop()
    {
        FirstTaskHandler3343.Count = 0;
        SecondTaskHandler3343.Count = 0;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<FirstTaskHandler3343>();
                opts.Discovery.IncludeType<SecondTaskHandler3343>();

                opts.Services.AddScoped<TaskQuery3343>();

                // The reporter's exact policies
                opts.Policies.OnException<ConcurrencyException3343>()
                    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            }).StartAsync();

        var tracked = await host
            .TrackActivity()
            .Timeout(15.Seconds())
            .SendMessageAndWaitAsync(new TaskCompleted3343(Guid.NewGuid()));

        // Each handler ran exactly once — no missing second handler, no runaway loop.
        FirstTaskHandler3343.Count.ShouldBe(1);
        SecondTaskHandler3343.Count.ShouldBe(1);

        // The message was delivered to each handler's own sticky local queue exactly once.
        var executed = tracked.Executed.MessagesOf<TaskCompleted3343>().ToList();
        executed.Count.ShouldBe(2);
    }
}

public record TaskCompleted3343(Guid Id);

public class TaskQuery3343
{
    public int Answer => 42;
}

public class ConcurrencyException3343 : Exception;

[WolverineIgnore] // keep out of other tests' conventional discovery; the test host adds it via IncludeType
public sealed class FirstTaskHandler3343
{
    public static int Count;

    private readonly ILogger<FirstTaskHandler3343> _logger;
    private readonly TaskQuery3343 _query;

    public FirstTaskHandler3343(ILogger<FirstTaskHandler3343> logger, TaskQuery3343 query)
    {
        _logger = logger;
        _query = query;
    }

    public Task HandleAsync(TaskCompleted3343 evt, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Count);
        _logger.LogInformation("First handled {Id} ({Answer})", evt.Id, _query.Answer);
        return Task.CompletedTask;
    }
}

[WolverineIgnore]
public class SecondTaskHandler3343
{
    public static int Count;

    private readonly TaskQuery3343 _query;
    private readonly ILogger<SecondTaskHandler3343> _logger;

    public SecondTaskHandler3343(TaskQuery3343 query, ILogger<SecondTaskHandler3343> logger)
    {
        _query = query;
        _logger = logger;
    }

    public Task HandleAsync(TaskCompleted3343 evt, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Count);
        _logger.LogInformation("Second handled {Id} ({Answer})", evt.Id, _query.Answer);
        return Task.CompletedTask;
    }
}
