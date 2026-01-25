using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Transports;

namespace Wolverine.Http.Tests.Transport;

public class Tenant
{
    public const string Id = "AwesomeTenant";
}

public class HttpScheduledMessageTests
{
    private IWolverineHttpTransportClient _transportClient =
        Substitute.For<IWolverineHttpTransportClient>();
    private async Task<IHost> CreateHostAsync()
    {
        var streamKey = $"scheduled-test-{Guid.NewGuid():N}";
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ScheduledTestService";
                opts.Services.AddSingleton<ScheduledMessageTracker>();
                opts.Services
                    .AddSingleton<IWolverineHttpTransportClient,
                        TestWolverineHttpTransportClient>();
                // Fast polling for tests
                opts.Durability.ScheduledJobFirstExecution = 50.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 100.Milliseconds();

                var transport = new HttpTransport();
                opts.Transports.Add(transport);
                opts.PublishAllMessages().ToHttpEndpoint(
                        "https://localhost:5000/wolverine/scheduled-test-endpoint",
                        true,
                        false)
                    .BufferedInMemory();
            }).StartAsync();
    }

    [Fact]
    public async Task should_delay_execution_of_scheduled_message()
    {
        using var host = await CreateHostAsync();
        var tracker =
            host.Services.GetRequiredService<ScheduledMessageTracker>();
        var tenantId = Guid.NewGuid().ToString();
        var bus = host.MessageBus();
        bus.TenantId = Tenant.Id;
        var count = 5;
        var i = count;
        while (i-- > 0)
        {
            var command = new ScheduledTestCommand(Guid.NewGuid().ToString());
            var scheduledTime = DateTimeOffset.UtcNow.AddSeconds(3);

            var startTime = DateTimeOffset.UtcNow;
            await bus.ScheduleAsync(command, scheduledTime);
        }

        await Task.Delay(500); // some delay for batching
        tracker.ReceivedMessages.Count.ShouldBe(count);
    }
}

public class TestWolverineHttpTransportClient : IWolverineHttpTransportClient
{
    private readonly ScheduledMessageTracker _tracker;
    public TestWolverineHttpTransportClient(ScheduledMessageTracker tracker)
    {
        _tracker = tracker;
    }
    public Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        foreach (var envelope in batch.Messages)
        {
            if (envelope.ScheduledTime.HasValue)
            {
                Assert.Equal(envelope.TenantId, Tenant.Id);
                _tracker.RecordExecution(envelope.Id.ToString());
            }
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(
        string uri,
        Envelope envelope,
        JsonSerializerOptions? options = null)
    {
        if (envelope.ScheduledTime.HasValue)
        {
            _tracker.RecordExecution(envelope.Id.ToString());
        }

        return Task.CompletedTask;
    }
}

public record ScheduledTestCommand(string Id);

public class ScheduledMessageTracker
{
    private readonly List<string> _receivedMessages = new();
    private readonly Dictionary<string, DateTimeOffset> _executionTimes = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> ReceivedMessages
    {
        get
        {
            lock (_lock)
            {
                return _receivedMessages.ToList();
            }
        }
    }

    public void RecordExecution(string messageId)
    {
        lock (_lock)
        {
            _receivedMessages.Add(messageId);
            _executionTimes[messageId] = DateTimeOffset.UtcNow;
        }
    }

    public DateTimeOffset GetExecutionTime(string messageId)
    {
        lock (_lock)
        {
            return _executionTimes[messageId];
        }
    }
}