using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Xunit;

namespace Wolverine.Redis.Tests;

public class StartFromBehaviorTests
{
    public record TestMessage(string Id);

    public class MessageHandler
    {
        private readonly MessageTracker _tracker;
        private readonly TaskCompletionSource<bool>? _tcs;
        public MessageHandler(MessageTracker tracker, TaskCompletionSource<bool>? tcs = null)
        {
            _tracker = tracker;
            _tcs = tcs;
        }
        
        public void Handle(TestMessage message)
        {
            _tracker.AddMessage(message.Id);
            _tcs?.TrySetResult(true);
        }
    }

    public class MessageTracker
    {
        private readonly List<string> _receivedMessages = new();
        public IReadOnlyList<string> ReceivedMessages => _receivedMessages.AsReadOnly();
        public void AddMessage(string id) => _receivedMessages.Add(id);
    }

    [Fact]
    public async Task StartFromNewMessages_should_only_process_messages_after_group_creation()
    {
        var streamKey = $"wolverine-tests-start-from-new-{Guid.NewGuid():N}";
        var tracker = new MessageTracker();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // First, send some messages before creating the listener
        using var publisherHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision();
            })
            .StartAsync();

        var bus = publisherHost.Services.GetRequiredService<IMessageBus>();
        
        // Send 3 messages before creating the consumer group
        await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}")).SendAsync(new TestMessage("before-1"));
        await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}")).SendAsync(new TestMessage("before-2"));
        await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}")).SendAsync(new TestMessage("before-3"));

        await publisherHost.StopAsync();

        // Now create a listener with StartFromNewMessages (default behavior)
        using var listenerHost = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                opts.ListenToRedisStream(streamKey, "test-group")
                    .StartFromNewMessages()  // Explicit, but this is the default
                    .BlockTimeout(TimeSpan.FromMilliseconds(100))
                    .DefaultIncomingMessage<TestMessage>();
                
                opts.Services.AddSingleton(tracker);
                opts.Services.AddSingleton(tcs);
                opts.Discovery.IncludeAssembly(typeof(StartFromBehaviorTests).Assembly);
            })
            .StartAsync();

        // Give listener time to start
        await Task.Delay(200);

        // Send a message after the listener is active
        var listenerBus = listenerHost.Services.GetRequiredService<IMessageBus>();
        await listenerBus.EndpointFor(new Uri($"redis://stream/0/{streamKey}")).SendAsync(new TestMessage("after-1"));

        // Wait for completion or timeout
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed == tcs.Task)
        {
            // Should only have received the message sent after listener creation
            tracker.ReceivedMessages.Count.ShouldBe(1);
            tracker.ReceivedMessages.ShouldContain("after-1");
            tracker.ReceivedMessages.ShouldNotContain("before-1");
            tracker.ReceivedMessages.ShouldNotContain("before-2");
            tracker.ReceivedMessages.ShouldNotContain("before-3");
        }
        else
        {
            // Fallback: just check what messages we got
            tracker.ReceivedMessages.Count.ShouldBe(1);
            tracker.ReceivedMessages.ShouldContain("after-1");
        }

        await listenerHost.StopAsync();
    }

    [Fact]
    public async Task StartFromBeginning_should_process_existing_messages()
    {
        var streamKey = $"wolverine-tests-start-from-beginning-{Guid.NewGuid():N}";
        var tracker = new MessageTracker();

        // First, send some messages before creating the listener
        using var publisherHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision();
            })
            .StartAsync();

        var bus = publisherHost.Services.GetRequiredService<IMessageBus>();
        
        // Send messages before creating the consumer group
        await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}")).SendAsync(new TestMessage("existing-1"));
        await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}")).SendAsync(new TestMessage("existing-2"));

        await publisherHost.StopAsync();

        // Now create a listener with StartFromBeginning
        using var listenerHost = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                var endpoint = opts.ListenToRedisStream(streamKey, "test-group-beginning")
                    .StartFromBeginning()  // Should process existing messages
                    .BlockTimeout(TimeSpan.FromMilliseconds(100))
                    .DefaultIncomingMessage<TestMessage>();
                
                opts.Services.AddSingleton(tracker);
                opts.Discovery.IncludeAssembly(typeof(StartFromBehaviorTests).Assembly);
            })
            .StartAsync();

        // Give time for message processing
        await Task.Delay(1000);

        // Should have received the existing messages
        tracker.ReceivedMessages.Count.ShouldBeGreaterThanOrEqualTo(2);
        tracker.ReceivedMessages.ShouldContain("existing-1");
        tracker.ReceivedMessages.ShouldContain("existing-2");

        await listenerHost.StopAsync();
    }
}
