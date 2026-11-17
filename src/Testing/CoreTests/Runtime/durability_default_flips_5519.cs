using CoreTests.Runtime;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5519. Four durability defaults flipped in 7.0: AlwaysMakeScheduledMessagesDurable, EnableInboxPartitioning,
/// DeadLetterQueueExpirationEnabled and InboxStaleTime / OutboxStaleTime. These cover how each one resolves, that an
/// explicit setting wins over RestoreV6Defaults() whichever is called first, and what each does in an application
/// without a message store. The with-a-store side lives in PostgresqlTests.
/// </summary>
public class durability_default_flips_5519
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_AlwaysMakeScheduledMessagesDurable_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        var on = explicitly(restoreFirst, o => o.Durability.AlwaysMakeScheduledMessagesDurable = true);
        on.Durability.AlwaysMakeScheduledMessagesDurable.ShouldBeTrue();

        var off = new WolverineOptions();
        off.Durability.AlwaysMakeScheduledMessagesDurable = false;
        off.Durability.AlwaysMakeScheduledMessagesDurable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void the_policy_method_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        var options = explicitly(restoreFirst, o => o.Policies.AlwaysMakeScheduledMessagesDurable());
        options.Durability.AlwaysMakeScheduledMessagesDurable.ShouldBeTrue();
        options.Durability.AlwaysMakeScheduledMessagesDurableExplicitly.ShouldBeTrue();
    }

    [Fact]
    public void the_7_0_default_of_AlwaysMakeScheduledMessagesDurable_is_not_an_explicit_opt_in()
    {
        // Only an explicit opt-in is warned about when there is no message store
        var options = new WolverineOptions();
        options.Durability.AlwaysMakeScheduledMessagesDurable.ShouldBeTrue();
        options.Durability.AlwaysMakeScheduledMessagesDurableExplicitly.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_EnableInboxPartitioning_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        var on = explicitly(restoreFirst, o => o.Durability.EnableInboxPartitioning = true);
        on.Durability.EnableInboxPartitioning.ShouldBeTrue();
        on.Durability.EnableInboxPartitioningExplicitly.ShouldBeTrue();

        var off = new WolverineOptions();
        off.Durability.EnableInboxPartitioning = false;
        off.Durability.EnableInboxPartitioning.ShouldBeFalse();
    }

    [Fact]
    public void the_7_0_default_of_EnableInboxPartitioning_does_not_reach_the_stores_other_than_postgresql()
    {
        // The other relational stores read EnableInboxPartitioningExplicitly, so their statements do not change
        var options = new WolverineOptions();
        options.Durability.EnableInboxPartitioning.ShouldBeTrue();
        options.Durability.EnableInboxPartitioningExplicitly.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_DeadLetterQueueExpirationEnabled_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        var on = explicitly(restoreFirst, o => o.Durability.DeadLetterQueueExpirationEnabled = true);
        on.Durability.DeadLetterQueueExpirationEnabled.ShouldBeTrue();

        var off = new WolverineOptions();
        off.Durability.DeadLetterQueueExpirationEnabled = false;
        off.Durability.DeadLetterQueueExpirationEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void an_explicit_stale_time_wins_over_RestoreV6Defaults(bool restoreFirst)
    {
        var options = explicitly(restoreFirst, o =>
        {
            o.Durability.InboxStaleTime = 10.Minutes();
            o.Durability.OutboxStaleTime = 20.Minutes();
        });

        options.Durability.InboxStaleTime.ShouldBe(10.Minutes());
        options.Durability.OutboxStaleTime.ShouldBe(20.Minutes());
    }

    [Fact]
    public void an_explicit_null_stale_time_turns_the_7_0_default_off()
    {
        var settings = new DurabilitySettings { InboxStaleTime = null, OutboxStaleTime = null };

        settings.InboxStaleTime.ShouldBeNull();
        settings.OutboxStaleTime.ShouldBeNull();
    }

    [Fact]
    public void serverless_with_outbox_keeps_its_own_stale_times_either_way()
    {
        var settings = new DurabilitySettings { Mode = DurabilityMode.ServerlessWithOutbox };
        settings.InboxStaleTime.ShouldBe(5.Minutes());
        settings.OutboxStaleTime.ShouldBe(60.Seconds());

        // As in 6.x, an explicit null there still means the mode's default
        settings.InboxStaleTime = null;
        settings.OutboxStaleTime = null;
        settings.InboxStaleTime.ShouldBe(5.Minutes());
        settings.OutboxStaleTime.ShouldBe(60.Seconds());

        var restored = new WolverineOptions();
        restored.RestoreV6Defaults();
        restored.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
        restored.Durability.InboxStaleTime.ShouldBe(5.Minutes());
        restored.Durability.OutboxStaleTime.ShouldBe(60.Seconds());
    }

    [Fact]
    public async Task without_a_message_store_scheduled_local_messages_stay_in_process_and_nothing_is_warned()
    {
        var logs = new DisposalAwareLoggerProvider();
        DefaultFlipsScheduledHandler.Received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DefaultFlipsScheduledHandler));
                opts.LocalQueueFor<DefaultFlipsScheduled>().BufferedInMemory();
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Storage.ShouldBeOfType<NullMessageStore>();
        runtime.Options.Durability.AlwaysMakeScheduledMessagesDurable.ShouldBeTrue();
        runtime.Options.Durability.DeadLetterQueueExpirationEnabled.ShouldBeTrue();
        runtime.Options.Durability.InboxStaleTime.ShouldBe(1.Hours());
        runtime.Options.Durability.OutboxStaleTime.ShouldBe(1.Hours());

        // The in-process scheduler still runs it: the default changes nothing without a store
        await host.MessageBus().ScheduleAsync(new DefaultFlipsScheduled(), 100.Milliseconds());
        await DefaultFlipsScheduledHandler.Received.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        logs.Messages.ShouldNotContain(x => x.Contains("AlwaysMakeScheduledMessagesDurable"));
    }

    [Fact]
    public async Task without_a_message_store_an_explicit_opt_in_is_still_warned_about()
    {
        var logs = new DisposalAwareLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Policies.AlwaysMakeScheduledMessagesDurable();
            }).StartAsync(TestContext.Current.CancellationToken);

        logs.Messages.ShouldContain(x => x.Contains("AlwaysMakeScheduledMessagesDurable() is set but no message store is configured"));
    }

    private static WolverineOptions explicitly(bool restoreFirst, Action<WolverineOptions> set)
    {
        var options = new WolverineOptions();
        if (restoreFirst) options.RestoreV6Defaults();
        set(options);
        if (!restoreFirst) options.RestoreV6Defaults();
        return options;
    }
}

public record DefaultFlipsScheduled;

public static class DefaultFlipsScheduledHandler
{
    public static TaskCompletionSource Received = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Handle(DefaultFlipsScheduled message) => Received.TrySetResult();
}
