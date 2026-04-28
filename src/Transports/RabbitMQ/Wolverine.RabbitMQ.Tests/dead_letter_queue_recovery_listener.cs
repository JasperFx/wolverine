using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.SqlServer;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// Tests for the DeadLetterQueueListener that recovers messages from RabbitMQ's
/// native dead letter queue into Wolverine's persistent dead letter storage.
///
/// This tests the EnableDeadLetterQueueRecovery() feature end-to-end:
/// 1. A message that always throws is published
/// 2. Wolverine NACKs it to RabbitMQ's native DLX (default behavior)
/// 3. The DeadLetterQueueListener picks it up from the DLQ
/// 4. The listener writes it to the PostgreSQL wolverine_dead_letters table
/// 5. The test queries the database and verifies the dead letter was recovered
/// </summary>
public class dead_letter_queue_recovery_listener : IAsyncLifetime
{
    private readonly string _queueName = $"dlq-recovery-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DlqRecoveryTest";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.EnableAutomaticFailureAcks = false;

                // Use SQL Server for message persistence
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dlq_recovery");

                // Use RabbitMQ with NATIVE dead letter queueing (the default)
                // PLUS enable recovery listener to bridge DLQ → database
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLetterQueueRecovery();

                opts.PublishMessage<RecoveryTestMessage>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName);

                opts.LocalRoutingConventionDisabled = true;
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.TeardownResources();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task recovers_native_dlq_message_to_database()
    {
        // Publish a message that will always fail — the handler throws DivideByZeroException.
        // With native DLQ mode, Wolverine NACKs it and RabbitMQ routes it to the DLX.
        // The DeadLetterQueueListener should pick it up and write to the database.
        await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new RecoveryTestMessage("test-recovery"));

        // Give the recovery listener time to pick up the message from RabbitMQ DLQ
        // and write it to the database
        var messageStore = _host.Services.GetRequiredService<IMessageStore>();
        var query = new DeadLetterEnvelopeQuery { PageSize = 100 };

        DeadLetterEnvelopeResults? results = null;
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());

        while (DateTimeOffset.UtcNow < deadline)
        {
            results = await messageStore.DeadLetters.QueryAsync(query, CancellationToken.None);
            if (results.Envelopes.Any()) break;
            await Task.Delay(500);
        }

        results.ShouldNotBeNull();
        results.Envelopes.ShouldNotBeEmpty(
            "The DeadLetterQueueListener should have recovered the failed message from " +
            "RabbitMQ's native dead letter queue into the database");

        // Verify the recovered dead letter has meaningful metadata
        var envelope = results.Envelopes.First();
        envelope.MessageType.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task recovers_multiple_messages()
    {
        // Send messages that will all fail — use the bus directly
        var bus = _host.Services.GetRequiredService<IMessageBus>();
        for (int i = 0; i < 3; i++)
        {
            await bus.PublishAsync(new RecoveryTestMessage($"multi-{i}"));
        }

        // Wait for recovery: messages fail → NACK to RabbitMQ DLQ → listener picks up → writes to DB
        var messageStore = _host.Services.GetRequiredService<IMessageStore>();
        var query = new DeadLetterEnvelopeQuery { PageSize = 100 };

        DeadLetterEnvelopeResults? results = null;
        var deadline = DateTimeOffset.UtcNow.Add(60.Seconds());

        while (DateTimeOffset.UtcNow < deadline)
        {
            results = await messageStore.DeadLetters.QueryAsync(query, CancellationToken.None);
            if (results.Envelopes.Count() >= 3) break;
            await Task.Delay(2.Seconds());
        }

        results.ShouldNotBeNull();
        results.Envelopes.Count().ShouldBeGreaterThanOrEqualTo(3,
            "All 3 failed messages should have been recovered from the RabbitMQ DLQ");
    }
}

/// <summary>
/// Tests the params string[] overload for custom queue names.
/// </summary>
public class dead_letter_queue_recovery_with_custom_queues : IAsyncLifetime
{
    private readonly string _queueName = $"custom-dlq-src-{Guid.NewGuid():N}";
    private readonly string _customDlqName = $"custom-dlq-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "CustomDlqRecoveryTest";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dlq_custom");

                // Configure a custom DLQ name on a specific queue
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLetterQueueRecovery(_customDlqName);

                opts.ListenToRabbitQueue(_queueName)
                    .DeadLetterQueueing(new DeadLetterQueue(_customDlqName, DeadLetterQueueMode.Native));

                opts.PublishMessage<CustomDlqTestMessage>().ToRabbitQueue(_queueName);

                opts.LocalRoutingConventionDisabled = true;
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.TeardownResources();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task recovers_from_custom_named_dlq()
    {
        await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new CustomDlqTestMessage("custom-test"));

        var messageStore = _host.Services.GetRequiredService<IMessageStore>();
        var query = new DeadLetterEnvelopeQuery { PageSize = 100 };

        DeadLetterEnvelopeResults? results = null;
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());

        while (DateTimeOffset.UtcNow < deadline)
        {
            results = await messageStore.DeadLetters.QueryAsync(query, CancellationToken.None);
            if (results.Envelopes.Any()) break;
            await Task.Delay(500);
        }

        results.ShouldNotBeNull();
        results.Envelopes.ShouldNotBeEmpty(
            "The DeadLetterQueueListener should recover messages from the custom-named DLQ");
    }

    [Fact]
    public void settings_contain_custom_queue_name()
    {
        var settings = _host.Services.GetRequiredService<DeadLetterQueueRecoverySettings>();
        settings.QueueNames.ShouldContain(_customDlqName);
    }
}

// Message types and handlers for the recovery tests

public record RecoveryTestMessage(string Value);

public static class RecoveryTestMessageHandler
{
    public static void Handle(RecoveryTestMessage message)
    {
        throw new DivideByZeroException($"Recovery test failure: {message.Value}");
    }
}

public record CustomDlqTestMessage(string Value);

public static class CustomDlqTestMessageHandler
{
    public static void Handle(CustomDlqTestMessage message)
    {
        throw new InvalidOperationException($"Custom DLQ test failure: {message.Value}");
    }
}
