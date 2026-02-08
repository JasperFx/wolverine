using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RateLimiting;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class rate_limiting_end_to_end
{
    [Fact]
    public async Task rate_limited_messages_are_delayed_over_rabbitmq()
    {
        var queueName = $"rate-limit-{Guid.NewGuid():N}";
        var schemaName = $"rate_limit_{Guid.NewGuid():N}";
        var tracker = new RateLimitTracker();
        var window = 1.Seconds();

        IHost? publisher = null;
        IHost? receiver = null;

        try
        {
            publisher = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                    opts.PublishAllMessages().ToRabbitQueue(queueName);
                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            receiver = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                opts.ApplicationAssembly = typeof(rate_limiting_end_to_end).Assembly;
                    opts.Services.AddSingleton(tracker);

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
                    opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToRabbitQueue(queueName).UseDurableInbox().Sequential();

                    opts.Policies.ForMessagesOfType<RateLimitedMessage>()
                        .RateLimit("rabbitmq-rate-limit", new RateLimit(1, window));

                    opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                }).StartAsync();

            await publisher.ResetResourceState();
            await receiver.ResetResourceState();
            await alignToWindowStart(window);

            var bus = publisher.MessageBus();
            await bus.PublishAsync(new RateLimitedMessage());
            await bus.PublishAsync(new RateLimitedMessage());

            var first = await tracker.FirstHandled.Task.WaitAsync(10.Seconds());
            var second = await tracker.SecondHandled.Task.WaitAsync(10.Seconds());

            (second - first).ShouldBeGreaterThanOrEqualTo(700.Milliseconds());
        }
        finally
        {
            if (receiver != null)
            {
                await safeStopAsync(receiver);
            }

            if (publisher != null)
            {
                await safeStopAsync(publisher);
            }
        }
    }

    private static async Task alignToWindowStart(TimeSpan window)
    {
        var windowTicks = window.Ticks;
        var thresholdTicks = 50.Milliseconds().Ticks;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (DateTimeOffset.UtcNow.Ticks % windowTicks < thresholdTicks)
            {
                return;
            }

            await Task.Delay(10.Milliseconds());
        }

        throw new TimeoutException("Could not align to rate limit window start.");
    }

    private static async Task safeStopAsync(IHost host)
    {
        try
        {
            await host.StopAsync();
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            host.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public record RateLimitedMessage;

public class RateLimitTracker
{
    private int _count;
    private readonly object _lock = new();

    public TaskCompletionSource<DateTimeOffset> FirstHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<DateTimeOffset> SecondHandled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void RecordHandled()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            _count++;
            if (_count == 1)
            {
                FirstHandled.TrySetResult(now);
            }
            else if (_count == 2)
            {
                SecondHandled.TrySetResult(now);
            }
        }
    }
}

public static class RateLimitedMessageHandler
{
    public static void Handle(RateLimitedMessage message, RateLimitTracker tracker)
    {
        tracker.RecordHandled();
    }
}
