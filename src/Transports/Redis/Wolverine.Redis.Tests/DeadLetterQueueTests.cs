using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Attributes;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Redis.Tests;

[Collection("DeadLetterQueueTests")]
public class DeadLetterQueueTests
{
    private readonly ITestOutputHelper _output;

    public DeadLetterQueueTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private async Task<(IHost host, string streamKey)> CreateHostAsync(bool enableDeadLetterQueue = true)
    {
        var streamKey = $"dlq-test-{Guid.NewGuid():N}";
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DeadLetterQueueTestService";
                
                // Disable automatic local queue processing
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.UseRedisTransport("localhost:6379").AutoProvision();
                
                // Configure routing to our test stream
                opts.PublishMessage<FailingCommand>().ToRedisStream(streamKey).SendInline();
                
                var listenerConfig = opts.ListenToRedisStream(streamKey, "dlq-test-group")
                    .StartFromBeginning();
                
                if (enableDeadLetterQueue)
                {
                    listenerConfig.EnableNativeDeadLetterQueue();
                }
                else
                {
                    listenerConfig.DisableNativeDeadLetterQueue();
                }
                
                opts.Services.AddSingleton<DeadLetterQueueTracker>();
            }).StartAsync();
        
        return (host, streamKey);
    }

    [Fact]
    public async Task failed_message_should_move_to_dead_letter_queue()
    {
        var (host, streamKey) = await CreateHostAsync(enableDeadLetterQueue: true);
        using var _ = host;
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: endpoint.DatabaseId);
        
        var deadLetterKey = endpoint.DeadLetterQueueKey;
        _output.WriteLine($"Dead letter key: {deadLetterKey}");
        _output.WriteLine($"Stream key: {endpoint.StreamKey}");
        
        // Clear the dead letter queue first
        await database.KeyDeleteAsync(deadLetterKey);
        
        // Send a message that will fail
        var bus = host.MessageBus();
        var command = new FailingCommand(Guid.NewGuid().ToString());
        _output.WriteLine($"Sending failing command: {command.Id}");
        await bus.PublishAsync(command);
        
        // Wait for processing and failure - need more time for retries to exhaust
        await Task.Delay(5000);
        
        var tracker = host.Services.GetRequiredService<DeadLetterQueueTracker>();
        _output.WriteLine($"Handler was called {tracker.Attempts.Count} times");
        
        // Verify message is in dead letter queue
        var deadLetterLength = await database.StreamLengthAsync(deadLetterKey);
        _output.WriteLine($"Dead letter queue length: {deadLetterLength}");
        
        deadLetterLength.ShouldBeGreaterThan(0, "Failed message should be in dead letter queue");
        
        _output.WriteLine($"✓ Message moved to dead letter queue (length: {deadLetterLength})");
        
        // Read the dead letter entry
        var deadLetterEntries = await database.StreamReadAsync(deadLetterKey, "0-0", count: 1);
        deadLetterEntries.Length.ShouldBeGreaterThan(0);
        
        var entry = deadLetterEntries[0];
        var values = entry.Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        
        values.ContainsKey("envelope-id").ShouldBeTrue();
        values.ContainsKey("exception-type").ShouldBeTrue();
        values.ContainsKey("exception-message").ShouldBeTrue();
        values.ContainsKey("message-type").ShouldBeTrue();
        values.ContainsKey("failed-at").ShouldBeTrue();
        
        _output.WriteLine($"✓ Dead letter entry contains all required fields");
        _output.WriteLine($"  Exception type: {values["exception-type"]}");
        _output.WriteLine($"  Exception message: {values["exception-message"]}");
    }

    [Fact]
    public async Task dead_letter_queue_should_contain_exception_details()
    {
        var (host, streamKey) = await CreateHostAsync(enableDeadLetterQueue: true);
        using var _ = host;
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: endpoint.DatabaseId);
        
        var deadLetterKey = endpoint.DeadLetterQueueKey;
        await database.KeyDeleteAsync(deadLetterKey);
        
        // Send a failing message
        var bus = host.MessageBus();
        var command = new FailingCommand(Guid.NewGuid().ToString(), "Test error message");
        await bus.PublishAsync(command);
        
        await Task.Delay(2000);
        
        // Read dead letter entry
        var deadLetterEntries = await database.StreamReadAsync(deadLetterKey, "0-0", count: 1);
        deadLetterEntries.Length.ShouldBeGreaterThan(0);
        
        var values = deadLetterEntries[0].Values.ToDictionary(v => v.Name.ToString(), v => v.Value.ToString());
        
        values["exception-type"].ShouldContain("InvalidOperationException");
        values["exception-message"].ShouldContain("Test error message");
        values.ContainsKey("exception-stack").ShouldBeTrue();
        
        _output.WriteLine($"✓ Exception details captured correctly");
    }

    [Fact]
    public async Task verify_native_dead_letter_queue_enabled_property()
    {
        var (host, streamKey) = await CreateHostAsync(enableDeadLetterQueue: true);
        using var _ = host;
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        
        endpoint.NativeDeadLetterQueueEnabled.ShouldBeTrue();
        
        _output.WriteLine($"✓ NativeDeadLetterQueueEnabled is true");
    }

    [Fact]
    public async Task verify_dead_letter_queue_key_format()
    {
        var (host, streamKey) = await CreateHostAsync();
        using var _ = host;
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        
        var deadLetterKey = endpoint.DeadLetterQueueKey;
        
        deadLetterKey.ShouldBe($"{streamKey}:dead-letter");
        
        _output.WriteLine($"✓ Dead letter key format: {deadLetterKey}");
    }

    [Fact]
    public async Task multiple_failed_messages_should_all_be_in_dead_letter_queue()
    {
        var (host, streamKey) = await CreateHostAsync(enableDeadLetterQueue: true);
        using var _ = host;
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        var database = transport.GetDatabase(database: endpoint.DatabaseId);
        
        var deadLetterKey = endpoint.DeadLetterQueueKey;
        await database.KeyDeleteAsync(deadLetterKey);
        
        // Send multiple failing messages
        var bus = host.MessageBus();
        for (int i = 0; i < 5; i++)
        {
            var command = new FailingCommand(Guid.NewGuid().ToString(), $"Error {i}");
            await bus.PublishAsync(command);
        }
        
        // Wait for all to fail
        await Task.Delay(3000);
        
        // Verify all are in dead letter queue
        var deadLetterLength = await database.StreamLengthAsync(deadLetterKey);
        deadLetterLength.ShouldBeGreaterThanOrEqualTo(5, "All failed messages should be in dead letter queue");
        
        _output.WriteLine($"✓ {deadLetterLength} messages in dead letter queue");
    }

    [Fact]
    public async Task disabled_dead_letter_queue_should_not_create_dead_letter_stream()
    {
        var (host, streamKey) = await CreateHostAsync(enableDeadLetterQueue: false);
        using var _ = host;
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();
        var endpoint = transport.StreamEndpoint(streamKey);
        
        endpoint.NativeDeadLetterQueueEnabled.ShouldBeFalse();
        
        _output.WriteLine($"✓ NativeDeadLetterQueueEnabled is false");
    }
}

public record FailingCommand(string Id, string? ErrorMessage = null);

public class FailingCommandHandler
{
    private readonly DeadLetterQueueTracker _tracker;

    public FailingCommandHandler(DeadLetterQueueTracker tracker)
    {
        _tracker = tracker;
    }

    [MaximumAttempts(1)]
    public void Handle(FailingCommand command)
    {
        _tracker.RecordAttempt(command.Id);
        throw new InvalidOperationException(command.ErrorMessage ?? "Intentional failure for testing");
    }
}

public class DeadLetterQueueTracker
{
    private readonly List<string> _attempts = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> Attempts
    {
        get
        {
            lock (_lock)
            {
                return _attempts.ToList();
            }
        }
    }

    public void RecordAttempt(string messageId)
    {
        lock (_lock)
        {
            _attempts.Add(messageId);
        }
    }
}

