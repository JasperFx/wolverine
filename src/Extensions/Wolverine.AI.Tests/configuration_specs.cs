using JasperFx.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AI.Internals;
using Wolverine.AI.Testing;
using Wolverine.Runtime;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace Wolverine.AI.Tests;

public class configuration_specs
{
    private static async Task<IHost> hostFor(Action<LlmCalloutOptions>? configure = null)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IChatClient>(new StubChatClient());
                opts.AddLlmCallouts(ai =>
                {
                    ai.DurableQueue = false;
                    configure?.Invoke(ai);
                });
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static LocalQueue calloutQueue(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        return runtime.Options.Transports.GetOrCreate<LocalTransport>().QueueFor(LlmCallout.QueueName);
    }

    [Fact]
    public async Task callouts_land_on_their_own_dedicated_local_queue()
    {
        using var host = await hostFor();

        var queue = calloutQueue(host);
        queue.MaxDegreeOfParallelism.ShouldBe(5);

        // The [LocalQueue] attribute on LlmCallout is what puts every callout on this one queue rather
        // than on a queue named after the message type.
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Options.Transports.GetOrCreate<LocalTransport>()
            .FindOrCreateQueueForMessageTypeByConvention(typeof(LlmCallout))
            .EndpointName.ShouldBe(LlmCallout.QueueName);
    }

    [Fact]
    public async Task the_parallelism_cap_is_configurable()
    {
        using var host = await hostFor(ai => ai.MaximumParallelCallouts = 2);

        calloutQueue(host).MaxDegreeOfParallelism.ShouldBe(2);
    }

    [Fact]
    public async Task opting_out_of_durability_gives_a_buffered_queue()
    {
        using var host = await hostFor();

        calloutQueue(host).Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public async Task the_queue_is_durable_by_default()
    {
        // Durability is the whole point of the tier: a callout returned from a handler is enrolled in
        // that handler's outbox, so it cannot fire for a transaction that did not commit.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IChatClient>(new StubChatClient());
                opts.AddLlmCallouts();
            }).StartAsync(TestContext.Current.CancellationToken);

        calloutQueue(host).Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public async Task ConfigureQueue_runs_after_the_defaults_so_it_wins()
    {
        using var host = await hostFor(ai =>
        {
            ai.MaximumParallelCallouts = 5;
            ai.ConfigureQueue(q => q.Sequential());
        });

        calloutQueue(host).MaxDegreeOfParallelism.ShouldBe(1);
    }

    [Fact]
    public async Task a_missing_IChatClient_fails_at_bootstrap_rather_than_on_the_first_callout()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.AddLlmCallouts())
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("IChatClient");
    }

    [Fact]
    public async Task calling_AddLlmCallouts_twice_is_an_error()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Services.AddSingleton<IChatClient>(new StubChatClient());
                    opts.AddLlmCallouts();
                    opts.AddLlmCallouts();
                }).StartAsync(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task the_budget_middleware_is_only_woven_in_when_a_budget_is_configured()
    {
        using var withoutBudget = await hostFor();
        chainFor(withoutBudget).Middleware.OfType<object>()
            .Any(x => x.ToString()!.Contains(nameof(LlmBudgetMiddleware))).ShouldBeFalse();

        using var withBudget = await hostFor(ai => ai.Budget.MaximumPromptCharacters = 1000);
        chainFor(withBudget).Middleware.OfType<object>()
            .Any(x => x.ToString()!.Contains(nameof(LlmBudgetMiddleware))).ShouldBeTrue();
    }

    private static Wolverine.Runtime.Handlers.HandlerChain chainFor(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        return runtime.Options.HandlerGraph.ChainFor(typeof(LlmCallout))!;
    }
}
