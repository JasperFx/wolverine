// BUG REPRO: RabbitMQ DLQ not saved to DB when using WolverineStorage
//
// This test demonstrates that failed RabbitMQ messages are not written to the SQL Server DLQ
// because DurableReceiver.NativeDeadLetterQueueEnabled is always true, regardless of queue.DeadLetterQueue.Mode.
// See: src/Wolverine/Runtime/WorkerQueues/DurableReceiver.cs
//
// Key findings:
// - Even when configuring opts.UseRabbitMq().DisableDeadLetterQueueing(),
//   so that queue.DeadLetterQueue.Mode == DeadLetterQueueMode.WolverineStorage,
//   failed messages are NOT written to the DB DLQ.
// - This is because DurableReceiver.NativeDeadLetterQueueEnabled is hardcoded to true,
//   so the wrong code path is taken in MoveToDeadLetterQueueAsync.
// - See the comments in the test and related code for more details.
//
// See also: https://github.com/JasperFx/wolverine/issues/<your-issue-number>

using System;
using System.Threading.Tasks;
using Dapper;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_DLQ_NotSavedToDatabase : IDisposable
{
    private readonly ITestOutputHelper _output;
    private IHost _host;

    public Bug_DLQ_NotSavedToDatabase(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        // Try to eliminate queues to keep them from accumulating
        _host?.TeardownResources();
        _host?.Dispose();
    }

    [Fact]
    public async Task test_1_durable_inbox_should_save_failed_messages_to_sql_dlq()
    {
        var queueName = $"durable-inbox-{Guid.NewGuid()}";
        var connectionString = Servers.SqlServerConnectionString;

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToRabbitQueue(queueName).UseDurableInbox();
                opts.PublishMessage<TestMessage>().ToRabbitQueue(queueName);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();

        // Debug print: check DeadLetterQueue and Mode
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];
        _output.WriteLine($"Queue '{queueName}'.DeadLetterQueue: {(queue.DeadLetterQueue == null ? "null" : queue.DeadLetterQueue.Mode.ToString())}");

        // Send message and verify DLQ behavior
        await SendMessageAndCheckDLQ(_host, connectionString, queueName, "durable inbox test");
    }

    [Fact]
    public async Task test_2_non_durable_inbox_should_save_failed_messages_to_sql_dlq()
    {
        var queueName = $"non-durable-inbox-{Guid.NewGuid()}";
        var connectionString = Servers.SqlServerConnectionString;

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToRabbitQueue(queueName); // No UseDurableInbox()
                opts.PublishMessage<TestMessage>().ToRabbitQueue(queueName);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();

        // Debug print: check DeadLetterQueue and Mode
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];
        _output.WriteLine($"Queue '{queueName}'.DeadLetterQueue: {(queue.DeadLetterQueue == null ? "null" : queue.DeadLetterQueue.Mode.ToString())}");

        // Send message and verify DLQ behavior
        await SendMessageAndCheckDLQ(_host, connectionString, queueName, "non-durable inbox test");
    }

    [Fact]
    public async Task test_3_global_durable_configs_should_save_failed_messages_to_sql_dlq()
    {
        var queueName = $"global-durable-configs-{Guid.NewGuid()}";
        var connectionString = Servers.SqlServerConnectionString;

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                
                // Global durable configs
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                
                opts.ListenToRabbitQueue(queueName);
                opts.PublishMessage<TestMessage>().ToRabbitQueue(queueName);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();

        // Debug print: check DeadLetterQueue and Mode
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];
        _output.WriteLine($"Queue '{queueName}'.DeadLetterQueue: {(queue.DeadLetterQueue == null ? "null" : queue.DeadLetterQueue.Mode.ToString())}");

        // Send message and verify DLQ behavior
        await SendMessageAndCheckDLQ(_host, connectionString, queueName, "global durable configs test");
    }

    // [Fact]
    // public async Task test_4_quorum_with_global_configs_should_save_failed_messages_to_sql_dlq()
    // {
    //     var queueName = $"quorum-global-configs-{Guid.NewGuid()}";
    //     var connectionString = Servers.SqlServerConnectionString;
    //
    //     _host = await Host.CreateDefaultBuilder()
    //         .UseWolverine(opts =>
    //         {
    //             opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
    //             opts.Policies.AutoApplyTransactions();
    //             opts.EnableAutomaticFailureAcks = false;
    //             opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup().UseQuorumQueues();
    //             
    //             // Global durable configs
    //             opts.Policies.UseDurableLocalQueues();
    //             opts.Policies.UseDurableInboxOnAllListeners();
    //             opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    //             
    //             opts.ListenToRabbitQueue(queueName);
    //             opts.PublishMessage<TestMessage>().ToRabbitQueue(queueName);
    //             opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
    //         }).StartAsync();
    //
    //     await _host.ResetResourceState();
    //
    //     // Debug print: check DeadLetterQueue and Mode
    //     var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    //     var transport = runtime.Options.RabbitMqTransport();
    //     var queue = transport.Queues[queueName];
    //     _output.WriteLine($"Queue '{queueName}'.DeadLetterQueue: {(queue.DeadLetterQueue == null ? "null" : queue.DeadLetterQueue.Mode.ToString())}");
    //
    //     // Send message and verify DLQ behavior
    //     await SendMessageAndCheckDLQ(_host, connectionString, queueName, "quorum with global configs test");
    // }

    private async Task SendMessageAndCheckDLQ(IHost host, string connectionString, string queueName, string testDescription)
    {
        // Get the message store from the host
        var messageStore = host.Services.GetRequiredService<IMessageStore>();
        
        // Print counts before sending message
        var initialCounts = await messageStore.Admin.FetchCountsAsync();
        _output.WriteLine($"Initial counts for {testDescription}: Incoming={initialCounts.Incoming}, Outgoing={initialCounts.Outgoing}, DeadLetter={initialCounts.DeadLetter}");

        // Send message using a scope
        using (var scope = host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            var message = new TestMessage { Id = Guid.NewGuid(), Content = $"{testDescription} message" };
            await bus.PublishAsync(message);

            // Wait for processing
            await Task.Delay(5000);

            // Check the DLQ using Wolverine's API instead of raw SQL
            var finalCounts = await messageStore.Admin.FetchCountsAsync();
            _output.WriteLine($"Final counts for {testDescription}: Incoming={finalCounts.Incoming}, Outgoing={finalCounts.Outgoing}, DeadLetter={finalCounts.DeadLetter}");
            
            // Query dead letters using Wolverine's API
            var deadLetterQuery = new DeadLetterEnvelopeQueryParameters
            {
                Limit = 100
            };
            var deadLetterResults = await messageStore.DeadLetters.QueryDeadLetterEnvelopesAsync(deadLetterQuery);
            
            _output.WriteLine($"Dead letter results for {testDescription}: {deadLetterResults.DeadLetterEnvelopes.Count} entries");
            
            // This should contain the failed message, but currently doesn't due to the bug
            deadLetterResults.DeadLetterEnvelopes.ShouldNotBeEmpty();
        }
    }

    // Handler that always throws to force DLQ
    public class TestHandler
    {
        public static Task Handle(TestMessage message)
        {
            throw new InvalidOperationException($"Simulated error for message {message.Id}");
        }
    }

    public record TestMessage
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = string.Empty;
    }
} 