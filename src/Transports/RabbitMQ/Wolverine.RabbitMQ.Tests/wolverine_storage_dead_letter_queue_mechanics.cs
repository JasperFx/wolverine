using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.SqlServer;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Xunit;
using IntegrationTests;
using Wolverine.Persistence.Durability;

namespace Wolverine.RabbitMQ.Tests;

public class wolverine_storage_dead_letter_queue_mechanics : IDisposable
{
    private readonly string QueueName = Guid.NewGuid().ToString();
    private IHost _host;
    private RabbitMqTransport theTransport;
    private readonly string connectionString;

    public wolverine_storage_dead_letter_queue_mechanics()
    {
        connectionString = Servers.SqlServerConnectionString;
    }

    public async Task afterBootstrapping()
    {
        await CreateHost(QueueName, useDurableInbox: false);
    }

    private async Task CreateHost(string queueName, bool useDurableInbox = false)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();

                if (useDurableInbox)
                {
                    opts.ListenToRabbitQueue(queueName).UseDurableInbox();
                    opts.PublishMessage<WolverineStorageTestMessage>().ToRabbitQueue(queueName);
                }
                else
                {
                    opts.PublishAllMessages().ToRabbitQueue(queueName);
                    opts.ListenToRabbitQueue(queueName);
                }

                opts.LocalRoutingConventionDisabled = true;
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();

        theTransport = _host
            .Services
            .GetRequiredService<IWolverineRuntime>()
            .Options
            .Transports
            .GetOrCreate<RabbitMqTransport>();
    }

    public void Dispose()
    {
        // Try to eliminate queues to keep them from accumulating
        _host?.TeardownResources();
        _host?.Dispose();
    }

    [Fact]
    public async Task should_not_have_native_dead_letter_objects_when_disabled()
    {
        await afterBootstrapping();

        theTransport.Exchanges.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();
        theTransport.Queues.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();
    }

    [Fact]
    public async Task should_not_set_dead_letter_queue_exchange_on_created_queues()
    {
        await afterBootstrapping();

        var queue = theTransport.Queues[QueueName];

        queue.Arguments.ContainsKey(RabbitMqTransport.DeadLetterQueueHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task should_have_wolverine_storage_mode()
    {
        await afterBootstrapping();

        var queue = theTransport.Queues[QueueName];
        queue.DeadLetterQueue?.Mode.ShouldBe(DeadLetterQueueMode.WolverineStorage);
    }

    [Fact]
    public async Task should_save_failed_messages_to_sql_server_dlq()
    {
        await afterBootstrapping();

        // Send a message that will fail
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new WolverineStorageTestMessage());

        // Wait a bit for processing
        await Task.Delay(1000);

        // Check that the message is in the SQL Server DLQ
        var messageStore = _host.Services.GetRequiredService<IMessageStore>();
        var deadLetterQuery = new DeadLetterEnvelopeQuery
        {
            PageSize = 100
        };
        var deadLetterResults = await messageStore.DeadLetters.QueryAsync(deadLetterQuery, CancellationToken.None);

        // Should have at least one dead letter message
        deadLetterResults.Envelopes.ShouldNotBeEmpty("Failed messages should be saved to SQL Server DLQ when using WolverineStorage mode");

        // Verify the message details
        var deadLetter = deadLetterResults.Envelopes.First();
        deadLetter.ExceptionType.ShouldBe(typeof(DivideByZeroException).FullName);
        deadLetter.ExceptionMessage.ShouldBe("Boom.");
    }

    [Fact]
    public async Task should_not_have_messages_in_rabbitmq_dlq()
    {
        await afterBootstrapping();

        // Send a message that will fail
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new WolverineStorageTestMessage());

        // Wait a bit for processing
        await Task.Delay(1000);

        // Check that there are no messages in any RabbitMQ DLQ
        // Since we disabled dead letter queueing, there shouldn't be any DLQ
        theTransport.Queues.Contains(RabbitMqTransport.DeadLetterQueueName).ShouldBeFalse();
    }

    [Fact]
    public async Task should_work_with_durable_inbox()
    {
        var durableQueueName = $"durable-{Guid.NewGuid()}";
        
        await CreateHost(durableQueueName, useDurableInbox: true);

        // Send a message that will fail
        await _host.TrackActivity().DoNotAssertOnExceptionsDetected().PublishMessageAndWaitAsync(new WolverineStorageTestMessage());

        // Wait a bit for processing
        await Task.Delay(1000);

        // Check that the message is in the SQL Server DLQ
        var messageStore = _host.Services.GetRequiredService<IMessageStore>();
        var deadLetterQuery = new DeadLetterEnvelopeQuery
        {
            PageSize = 100
        };
        var deadLetterResults = await messageStore.DeadLetters.QueryAsync(deadLetterQuery, CancellationToken.None);

        // Should have at least one dead letter message
        deadLetterResults.Envelopes.ShouldNotBeEmpty("Failed messages should be saved to SQL Server DLQ even with durable inbox");
    }
}

public record WolverineStorageTestMessage;

public static class WolverineStorageTestHandler
{
    public static void Handle(WolverineStorageTestMessage command)
    {
        throw new DivideByZeroException("Boom.");
    }
} 