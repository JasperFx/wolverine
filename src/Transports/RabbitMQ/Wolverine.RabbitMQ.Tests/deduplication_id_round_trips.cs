using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// D2 (durable compliance findings, 2026-10-22). <see cref="Envelope.DeduplicationId" /> did not cross RabbitMQ: the
/// generic envelope mapper wrote no <c>deduplication-id</c> header, so a <c>[Deduplicated]</c> handler behind a
/// Rabbit queue dead-lettered every copy with <c>MissingDeduplicationIdException</c>.
/// </summary>
public class deduplication_id_round_trips : IAsyncLifetime
{
    private readonly string _queueName = $"dedup-id-{Guid.NewGuid():N}";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DeduplicationIdMessageHandler));

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.PublishMessage<DeduplicationIdMessage>().ToRabbitQueue(_queueName);
                opts.ListenToRabbitQueue(_queueName);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_deduplication_id_set_by_the_sender_reaches_the_receiver()
    {
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new DeduplicationIdMessage("one"),
                new DeliveryOptions { DeduplicationId = "invoice-17|José" });

        session.Received.SingleEnvelope<DeduplicationIdMessage>()
            .DeduplicationId.ShouldBe("invoice-17|José");
    }
}

public record DeduplicationIdMessage(string Name);

public static class DeduplicationIdMessageHandler
{
    public static void Handle(DeduplicationIdMessage message)
    {
        // Nothing to do: the test reads the received envelope from the tracked session
    }
}
