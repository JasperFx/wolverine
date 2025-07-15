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
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_DLQ_NotSavedToDatabase
{
    private readonly ITestOutputHelper _output;

    public Bug_DLQ_NotSavedToDatabase(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task message_that_fails_in_handler_should_be_saved_to_sql_dlq_but_is_not()
    {
        var staticQueue = "static-test-queue";
        var instanceQueue = "instance-test-queue";
        var connectionString = Servers.SqlServerConnectionString;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(connectionString, "wolverine");
                opts.Policies.AutoApplyTransactions();
                opts.EnableAutomaticFailureAcks = false;
                opts.UseRabbitMq().DisableDeadLetterQueueing().AutoProvision();
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
                opts.ListenToRabbitQueue(staticQueue);
                opts.ListenToRabbitQueue(instanceQueue);
                opts.PublishMessage<TestMessage>().ToRabbitQueue(staticQueue);
                opts.PublishMessage<TestMessage>().ToRabbitQueue(instanceQueue);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await host.ResetResourceState();

        // Debug print: check DeadLetterQueue and Mode for both queues
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.RabbitMqTransport();
        var staticQ = transport.Queues[staticQueue];
        var instanceQ = transport.Queues[instanceQueue];
        _output.WriteLine($"staticQueue.DeadLetterQueue: {(staticQ.DeadLetterQueue == null ? "null" : staticQ.DeadLetterQueue.Mode.ToString())}");
        _output.WriteLine($"instanceQueue.DeadLetterQueue: {(instanceQ.DeadLetterQueue == null ? "null" : instanceQ.DeadLetterQueue.Mode.ToString())}");

        // Print all tables in the current database for debugging
        await using (var debugConn = new SqlConnection(connectionString))
        {
            await debugConn.OpenAsync();
            var tables = await debugConn.QueryAsync<string>(
                "SELECT TABLE_SCHEMA + '.' + TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'");
            _output.WriteLine("Tables in database:");
            foreach (var table in tables)
            {
                _output.WriteLine(table);
            }
        }

        // Send messages to both queues using a scope
        using (var scope = host.Services.CreateScope())
        {
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            var staticMessage = new TestMessage { Id = Guid.NewGuid(), Content = "Static handler test message" };
            var instanceMessage = new TestMessage { Id = Guid.NewGuid(), Content = "Instance handler test message" };
            await bus.PublishAsync(staticMessage);
            await bus.PublishAsync(instanceMessage);

            // Wait for processing
            await Task.Delay(30000);

            // Check the DLQ table for the messages
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            // Print the count and all entries in the DLQ table for debugging
            var dlqCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM wolverine.wolverine_dead_letters");
            _output.WriteLine($"DLQ count: {dlqCount}");
            var allDeadLetters = await conn.QueryAsync($"SELECT * FROM wolverine.wolverine_dead_letters");
            foreach (var row in allDeadLetters)
            {
                _output.WriteLine(row.ToString());
            }
            allDeadLetters.ShouldNotBeEmpty();
        }
    }

    // Handler that always throws to force DLQ
    public class StaticTestHandler
    {
        public static Task Handle(TestMessage message)
        {
            throw new InvalidOperationException($"Simulated error for message {message.Id} in STATIC handler");
        }
    }

    public class InstanceTestHandler
    {
        public Task Handle(TestMessage message)
        {
            throw new InvalidOperationException($"Simulated error for message {message.Id} in INSTANCE handler");
        }
    }

    public record TestMessage
    {
        public Guid Id { get; init; }
        public string Content { get; init; } = string.Empty;
    }
} 