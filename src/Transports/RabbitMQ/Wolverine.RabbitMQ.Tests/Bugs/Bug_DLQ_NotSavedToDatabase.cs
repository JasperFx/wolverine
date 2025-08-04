// BUG REPRO: RabbitMQ DLQ behavior investigation
//
// This test investigates the behavior of failed RabbitMQ messages when using different DLQ configurations.
// The current behavior shows that messages are not written to the SQL Server DLQ when using .DisableDeadLetterQueueing().
//
// Current behavior:
// - When configuring opts.UseRabbitMq().DisableDeadLetterQueueing(),
//   so that queue.DeadLetterQueue.Mode == DeadLetterQueueMode.WolverineStorage,
//   failed messages are NOT written to the DB DLQ.
// - This appears to be because RabbitMqQueue.TryBuildDeadLetterSender always returns true,
//   causing DurableReceiver._deadLetterSender to always be non-null.
// - As a result, DurableReceiver.MoveToErrorsAsync always takes the RabbitMQ DLQ path
//   instead of the SQL Server DLQ path.
//
// Investigation: This test demonstrates the current behavior and investigates whether
// this is the intended design or if there's a configuration issue.
//
// See also: https://github.com/JasperFx/wolverine/issues/1568

using System;
using System.Threading.Tasks;
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
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Transports.Sending;
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



    [Fact]
    public async Task test_5_verify_try_build_dead_letter_sender_behavior()
    {
        var connectionString = Servers.SqlServerConnectionString;

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();

        // Test 1: Queue with WolverineStorage mode
        var queue1 = new RabbitMqQueue("test-queue-1", transport);
        queue1.DeadLetterQueue = new DeadLetterQueue("test-dlq-1", DeadLetterQueueMode.WolverineStorage);
        
        var hasDeadLetterSender1 = queue1.TryBuildDeadLetterSender(runtime, out var deadLetterSender1);
        _output.WriteLine($"Queue with WolverineStorage mode - TryBuildDeadLetterSender: {hasDeadLetterSender1}, sender null: {deadLetterSender1 == null}");
        
        // Assert that TryBuildDeadLetterSender should return false for WolverineStorage mode
        // This test will fail, demonstrating the bug
        hasDeadLetterSender1.ShouldBeFalse("TryBuildDeadLetterSender should return false for WolverineStorage mode to allow SQL Server DLQ path");
        deadLetterSender1.ShouldBeNull("Dead letter sender should be null for WolverineStorage mode");

        // Test 2: Queue with Native mode
        var queue2 = new RabbitMqQueue("test-queue-2", transport);
        queue2.DeadLetterQueue = new DeadLetterQueue("test-dlq-2", DeadLetterQueueMode.Native);
        
        var hasDeadLetterSender2 = queue2.TryBuildDeadLetterSender(runtime, out var deadLetterSender2);
        _output.WriteLine($"Queue with Native mode - TryBuildDeadLetterSender: {hasDeadLetterSender2}, sender null: {deadLetterSender2 == null}");
        
        // This should be true for Native mode
        hasDeadLetterSender2.ShouldBeTrue("TryBuildDeadLetterSender should return true for Native mode");
        deadLetterSender2.ShouldNotBeNull("Dead letter sender should not be null for Native mode");

        // Test 3: Queue with no dead letter queue
        var queue3 = new RabbitMqQueue("test-queue-3", transport);
        queue3.DeadLetterQueue = null;
        
        var hasDeadLetterSender3 = queue3.TryBuildDeadLetterSender(runtime, out var deadLetterSender3);
        _output.WriteLine($"Queue with no DLQ - TryBuildDeadLetterSender: {hasDeadLetterSender3}, sender null: {deadLetterSender3 == null}");
        
        // This should be false when no DLQ is configured
        hasDeadLetterSender3.ShouldBeFalse("TryBuildDeadLetterSender should return false when no DLQ is configured");
        deadLetterSender3.ShouldBeNull("Dead letter sender should be null when no DLQ is configured");

        // Summary of findings
        _output.WriteLine("");
        _output.WriteLine("Summary:");
        _output.WriteLine("- TryBuildDeadLetterSender returns true for all modes (WolverineStorage, Native, and no DLQ)");
        _output.WriteLine("- This means DurableReceiver._deadLetterSender is always non-null");
        _output.WriteLine("- As a result, DurableReceiver.MoveToErrorsAsync always uses the RabbitMQ DLQ path");
        _output.WriteLine("- The SQL Server DLQ path is never taken, regardless of DeadLetterQueueMode");
    }

    [Fact]
    public async Task test_6_demonstrate_durable_receiver_decision_logic_issue()
    {
        var queueName = $"decision-logic-test-{Guid.NewGuid()}";
        var connectionString = Servers.SqlServerConnectionString;

        _output.WriteLine($"Starting decision logic test with queue: {queueName}");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToRabbitQueue(queueName);
                opts.PublishMessage<TestMessage>().ToRabbitQueue(queueName);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];
        
        _output.WriteLine($"Queue '{queueName}'.DeadLetterQueue: {(queue.DeadLetterQueue == null ? "null" : queue.DeadLetterQueue.Mode.ToString())}");

        // Verify that TryBuildDeadLetterSender returns true
        // even when DeadLetterQueueMode.WolverineStorage is configured
        var hasDeadLetterSender = queue.TryBuildDeadLetterSender(runtime, out var deadLetterSender);
        _output.WriteLine($"TryBuildDeadLetterSender returned: {hasDeadLetterSender}, sender is null: {deadLetterSender == null}");
        
        // Assert that TryBuildDeadLetterSender should return false for WolverineStorage mode
        // This test will fail, demonstrating the bug
        hasDeadLetterSender.ShouldBeFalse("TryBuildDeadLetterSender should return false for WolverineStorage mode to allow SQL Server DLQ path");
        deadLetterSender.ShouldBeNull("Dead letter sender should be null for WolverineStorage mode");

        // This demonstrates the decision logic in DurableReceiver.MoveToErrorsAsync
        _output.WriteLine("");
        _output.WriteLine("DurableReceiver.MoveToErrorsAsync decision logic:");
        _output.WriteLine("if (_deadLetterSender != null) {");
        _output.WriteLine("    await _deadLetterSender.SendAsync(envelope); // RabbitMQ DLQ path");
        _output.WriteLine("    return;");
        _output.WriteLine("}");
        _output.WriteLine("// SQL Server DLQ path");
        _output.WriteLine("await _inbox.MoveToDeadLetterStorageAsync(report.Envelope, report.Exception);");
        _output.WriteLine("");
        _output.WriteLine($"Since _deadLetterSender is {(deadLetterSender == null ? "null" : "non-null")}, the code will take the {(deadLetterSender == null ? "SQL Server DLQ" : "RabbitMQ DLQ")} path.");
        _output.WriteLine("");
        _output.WriteLine("The bug is that TryBuildDeadLetterSender returns true for WolverineStorage mode,");
        _output.WriteLine("causing the RabbitMQ DLQ path to always be taken instead of the SQL Server DLQ path.");
    }

    private async Task SendMessageAndCheckDLQ(IHost host, string connectionString, string queueName, string testDescription)
    {
        // Get the message store from the host
        var messageStore = host.Services.GetRequiredService<IMessageStore>();
        
        // Debug: Check if a dead letter sender was created
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();
        var queue = transport.Queues[queueName];
        
        // Try to build a dead letter sender to see what happens
        var hasDeadLetterSender = queue.TryBuildDeadLetterSender(runtime, out var deadLetterSender);
        _output.WriteLine($"Queue '{queueName}' TryBuildDeadLetterSender returned: {hasDeadLetterSender}, sender is null: {deadLetterSender == null}");
        
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
            
            // Document the current behavior - the DLQ is empty
            _output.WriteLine($"Current behavior: SQL Server DLQ contains {deadLetterResults.DeadLetterEnvelopes.Count} messages");
            _output.WriteLine("Note: The message appears to have been sent to the RabbitMQ DLQ instead of the SQL Server DLQ");
            
            // Assert that we should have DLQ entries in SQL Server when using WolverineStorage mode
            // This test will fail, demonstrating the current bug
            deadLetterResults.DeadLetterEnvelopes.ShouldNotBeEmpty($"When using DeadLetterQueueMode.WolverineStorage, failed messages should be saved to the SQL Server DLQ, but found {deadLetterResults.DeadLetterEnvelopes.Count} entries");
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