using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// End-to-end coverage for the SQS <c>EnableDeadLetterQueueRecovery()</c> bridge added for #3103:
/// a failing message is moved to the native SQS dead letter queue, the background recovery listener
/// drains it, and the dead letter ends up queryable in Wolverine's durable storage.
/// </summary>
public class dead_letter_queue_recovery : IAsyncLifetime
{
    private readonly string _queueName = $"dlq-recovery-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "SqsDlqRecoveryTest";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLetterQueueRecovery();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "sqs_dlq_recovery";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.ListenToSqsQueue(_queueName);
                opts.PublishMessage<SqsRecoveryTestMessage>().ToSqsQueue(_queueName);

                opts.Policies.DisableConventionalLocalRouting();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task recovers_native_dlq_message_to_durable_storage()
    {
        await _host
            .TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .Timeout(30.Seconds())
            .PublishMessageAndWaitAsync(new SqsRecoveryTestMessage("test-recovery"));

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
            "The SQS dead letter recovery listener should have recovered the failed message into durable storage");

        var envelope = results.Envelopes.First();
        envelope.MessageType.ShouldNotBeNullOrEmpty();
    }
}

public record SqsRecoveryTestMessage(string Value);

public static class SqsRecoveryTestMessageHandler
{
    public static void Handle(SqsRecoveryTestMessage message)
    {
        throw new DivideByZeroException($"SQS recovery test failure: {message.Value}");
    }
}
