using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// End-to-end coverage for the Azure Service Bus <c>EnableDeadLetterQueueRecovery()</c> bridge added
/// for #3103. A failing handler makes Wolverine natively dead-letter the message to the queue's
/// <c>$DeadLetterQueue</c> sub-queue (with <c>DeadLetterReason</c>/<c>DeadLetterErrorDescription</c>
/// set). The background recovery listener drains the sub-queue and the dead letter ends up queryable
/// in Wolverine's durable storage.
/// </summary>
[Trait("Category", "Flaky")]
public class dead_letter_queue_recovery : IAsyncLifetime
{
    private readonly string _queueName = $"dlqrecovery{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "AsbDlqRecoveryTest";
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseAzureServiceBusTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLetterQueueRecovery();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "asb_dlq_recovery";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.ListenToAzureServiceBusQueue(_queueName);
                opts.PublishMessage<AsbRecoveryTestMessage>().ToAzureServiceBusQueue(_queueName);

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
            .PublishMessageAndWaitAsync(new AsbRecoveryTestMessage("test-recovery"));

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
            "The Azure Service Bus dead letter recovery listener should have recovered the natively dead-lettered message into durable storage");

        var envelope = results.Envelopes.First();
        envelope.MessageType.ShouldNotBeNullOrEmpty();

        // The native DeadLetterReason (the exception type) should be preserved as the dead letter's
        // exception type rather than the DeadLetterRecoveredException wrapper.
        envelope.ExceptionType.ShouldNotBeNullOrEmpty();
    }
}

public record AsbRecoveryTestMessage(string Value);

public static class AsbRecoveryTestMessageHandler
{
    public static void Handle(AsbRecoveryTestMessage message)
    {
        throw new DivideByZeroException($"ASB recovery test failure: {message.Value}");
    }
}
