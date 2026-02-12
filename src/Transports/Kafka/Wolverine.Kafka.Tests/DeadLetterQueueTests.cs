using System.Text;
using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class DeadLetterQueueTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _host;
    private readonly string _topicName;
    private readonly string _dlqTopicName;

    public DeadLetterQueueTests(ITestOutputHelper output)
    {
        _output = output;
        _topicName = $"dlq-test-{Guid.NewGuid():N}";
        _dlqTopicName = "wolverine-dead-letter-queue";
    }

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092")
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);

                opts.ListenToKafkaTopic(_topicName)
                    .ProcessInline()
                    .EnableNativeDeadLetterQueue();

                opts.PublishMessage<DlqTestMessage>()
                    .ToKafkaTopic(_topicName)
                    .SendInline();

                opts.Policies.OnException<AlwaysFailException>().MoveToErrorQueue();

                opts.Discovery.IncludeAssembly(GetType().Assembly);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private ConsumeResult<string, byte[]>? ConsumeDlqMessage(TimeSpan timeout)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = "localhost:9092",
            GroupId = $"dlq-verify-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(_dlqTopicName);

        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var result = consumer.Consume(TimeSpan.FromSeconds(5));
                if (result != null) return result;
            }
            catch (ConsumeException)
            {
                // Retry on transient errors
            }
        }

        return null;
    }

    [Fact]
    public async Task failed_message_moves_to_dead_letter_queue()
    {
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(ctx => ctx.PublishAsync(new DlqTestMessage("fail-me")));

        // Verify Wolverine tracked MovedToErrorQueue
        var movedRecord = session.AllRecordsInOrder()
            .FirstOrDefault(x => x.MessageEventType == MessageEventType.MovedToErrorQueue);
        movedRecord.ShouldNotBeNull("Expected message to be moved to error queue");

        // Consume from DLQ topic and verify message arrived
        var result = ConsumeDlqMessage(30.Seconds());
        result.ShouldNotBeNull("Expected message on DLQ Kafka topic");
        result.Message.ShouldNotBeNull();
        result.Message.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task dead_letter_message_has_exception_headers()
    {
        await _host.TrackActivity()
            .IncludeExternalTransports()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<DlqTestMessage>(_host)
            .PublishMessageAndWaitAsync(new DlqTestMessage("fail-headers"));

        var result = ConsumeDlqMessage(30.Seconds());
        result.ShouldNotBeNull("Expected message on DLQ Kafka topic");

        var headers = result.Message.Headers;
        headers.ShouldNotBeNull();

        GetHeaderValue(headers, "exception-type").ShouldContain("AlwaysFailException");
        GetHeaderValue(headers, "exception-message").ShouldNotBeNullOrEmpty();
        GetHeaderValue(headers, "exception-stack").ShouldNotBeNullOrEmpty();
        GetHeaderValue(headers, "failed-at").ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void native_dlq_disabled_by_default()
    {
        var transport = new KafkaTransport();
        var topic = new KafkaTopic(transport, "test-topic", EndpointRole.Application);
        topic.NativeDeadLetterQueueEnabled.ShouldBeFalse();
    }

    [Fact]
    public void default_dlq_topic_name()
    {
        var transport = new KafkaTransport();
        transport.DeadLetterQueueTopicName.ShouldBe("wolverine-dead-letter-queue");
    }

    [Fact]
    public void custom_dlq_topic_name()
    {
        var transport = new KafkaTransport();
        transport.DeadLetterQueueTopicName = "my-custom-dlq";
        transport.DeadLetterQueueTopicName.ShouldBe("my-custom-dlq");
    }

    private static string GetHeaderValue(Headers headers, string key)
    {
        if (headers.TryGetLastBytes(key, out var bytes))
        {
            return Encoding.UTF8.GetString(bytes);
        }

        return string.Empty;
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}

public record DlqTestMessage(string Id);

public class AlwaysFailException : Exception
{
    public AlwaysFailException(string message) : base(message)
    {
    }
}

public static class DlqTestMessageHandler
{
    public static void Handle(DlqTestMessage message)
    {
        throw new AlwaysFailException($"Intentional failure for DLQ testing: {message.Id}");
    }
}
