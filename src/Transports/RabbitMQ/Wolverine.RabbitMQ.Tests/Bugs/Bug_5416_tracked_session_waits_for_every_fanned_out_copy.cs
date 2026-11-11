using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

/// <summary>
/// GH-5416. With separated handlers and conventional routing by handler type, one message published to the
/// exchange is fanned out by RabbitMQ to one queue per handler, and every copy has the same envelope id. The
/// tracked session cannot know how the broker fans a message out, so the test tells it with a custom condition.
/// </summary>
public class Bug_5416_tracked_session_waits_for_every_fanned_out_copy
{
    [Fact]
    public async Task the_session_waits_for_the_slow_handler()
    {
        FanOut5416SlowHandler.Handled.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .UseConventionalRouting(x =>
                    {
                        x.IncludeTypes(type => type == typeof(FanOut5416));
                    });

                opts.Policies.DisableConventionalLocalRouting();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FanOut5416FastHandler>()
                    .IncludeType<FanOut5416SlowHandler>();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        #region sample_waiting_for_a_message_fanned_out_by_the_broker

        var message = new FanOut5416(Guid.NewGuid());

        var tracked = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())

            // RabbitMQ delivers one copy of the message to each queue bound to the exchange, and
            // the tracked session has no way to know how many queues that is. Tell it instead:
            // keep waiting until the message has been handled successfully at both queues
            .WaitForCondition(new MessageSucceededAtEveryQueue<FanOut5416>(2))
            .SendMessageAndWaitAsync(message);

        #endregion

        FanOut5416SlowHandler.Handled.ShouldContain(message.Id);
        tracked.Executed.MessagesOf<FanOut5416>().Count().ShouldBe(2);
    }
}

#region sample_tracked_condition_for_a_message_fanned_out_by_the_broker

/// <summary>
/// Holds a tracked session open until a message of type T has been handled successfully at
/// the expected number of distinct receiving endpoints
/// </summary>
public class MessageSucceededAtEveryQueue<T> : ITrackedCondition
{
    private readonly int _expected;
    private readonly HashSet<Uri> _queues = new();
    private readonly object _lock = new();

    public MessageSucceededAtEveryQueue(int expected)
    {
        _expected = expected;
    }

    public void Record(EnvelopeRecord record)
    {
        if (record.MessageEventType != MessageEventType.MessageSucceeded) return;
        if (record.Message is not T) return;
        if (record.Envelope?.Destination is not { } queue) return;

        lock (_lock)
        {
            _queues.Add(queue);
        }
    }

    public bool IsCompleted()
    {
        lock (_lock)
        {
            return _queues.Count >= _expected;
        }
    }
}

#endregion

public record FanOut5416(Guid Id);

[StickyHandler(nameof(FanOut5416FastHandler))]
public class FanOut5416FastHandler : IWolverineHandler
{
    public void Handle(FanOut5416 message)
    {
    }
}

[StickyHandler(nameof(FanOut5416SlowHandler))]
public class FanOut5416SlowHandler : IWolverineHandler
{
    public static readonly System.Collections.Concurrent.ConcurrentBag<Guid> Handled = new();

    public async Task Handle(FanOut5416 message)
    {
        await Task.Delay(1500);
        Handled.Add(message.Id);
    }
}
