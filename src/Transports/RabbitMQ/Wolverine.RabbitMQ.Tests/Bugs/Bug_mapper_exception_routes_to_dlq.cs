using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_mapper_exception_routes_to_dlq : IDisposable
{
    private readonly string _queueName = "mapper_explosion_" + Guid.NewGuid().ToString("N");
    private IHost _host = null!;

    public void Dispose()
    {
        _host?.TeardownResources();
        _host?.Dispose();
    }

    [Fact]
    public async Task unmappable_message_is_routed_to_broker_dlq_not_silently_acked()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.ListenToRabbitQueue(_queueName)
                    .UseInterop(new AlwaysThrowingMapper());
            }).StartAsync();

        var transport = _host.Services
            .GetRequiredService<IWolverineRuntime>()
            .Options
            .Transports
            .GetOrCreate<RabbitMqTransport>();

        // Publish via the admin channel so we bypass Wolverine's outgoing mapper —
        // the bug under test is on the receiving side when the incoming mapper throws.
        await transport.WithAdminChannelAsync(async channel =>
        {
            var props = new BasicProperties { MessageId = Guid.NewGuid().ToString() };

            await channel.BasicPublishAsync(
                exchange: "",
                routingKey: _queueName,
                mandatory: false,
                basicProperties: props,
                body: "hello"u8.ToArray());
        });

        // The default dead-letter queue that Wolverine provisions automatically
        var deadLetterQueue = transport.Queues[RabbitMqTransport.DeadLetterQueueName];

        var attempts = 0;
        while (attempts < 40)
        {
            var count = await deadLetterQueue.QueuedCountAsync();
            if (count > 0) return;
            attempts++;
            await Task.Delay(250.Milliseconds());
        }

        throw new Exception(
            $"Expected unmappable message to arrive in {RabbitMqTransport.DeadLetterQueueName}, but it never did (silent loss).");
    }
}

internal class AlwaysThrowingMapper : IRabbitMqEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, IBasicProperties outgoing)
    {
    }

    public void MapIncomingToEnvelope(Envelope envelope, IReadOnlyBasicProperties incoming)
    {
        throw new InvalidOperationException("simulated mapper failure");
    }
}
