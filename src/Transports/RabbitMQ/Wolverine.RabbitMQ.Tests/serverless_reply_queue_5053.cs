using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5053. The serverless modes listen for no replies, but the RabbitMQ transport still declared a system reply
/// queue (wolverine.response.&lt;guid&gt;) on every cold start, and every outgoing envelope carried a reply-uri
/// pointing at it. Under Serverless and ServerlessWithOutbox the transport now skips the reply queue, the same as
/// DisableSystemRequestReplyQueueDeclaration()
/// </summary>
public class serverless_reply_queue_5053
{
    private const string SchemaName = "gh5053_rmq";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(DurabilityMode.Serverless, false)]
    [InlineData(DurabilityMode.ServerlessWithOutbox, false)]
    [InlineData(DurabilityMode.Solo, true)]
    [InlineData(DurabilityMode.Balanced, true)]
    public async Task the_system_reply_queue_is_only_declared_outside_the_serverless_modes(DurabilityMode mode,
        bool expectReplyQueue)
    {
        var queueName = $"gh5053-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery();

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                opts.UseRabbitMq().AutoProvision();
                opts.PublishMessage<ReplyQueueEvent>().ToRabbitQueue(queueName);
            }).StartAsync(Token);

        var transport = host.GetRuntime().Options.RabbitMqTransport();
        var responseQueues = transport.Queues.Where(x => x.QueueName.StartsWith("wolverine.response.")).ToArray();

        await host.MessageBus().PublishAsync(new ReplyQueueEvent(Guid.NewGuid()));

        var factory = new ConnectionFactory { HostName = "localhost" };
        await using var connection = await factory.CreateConnectionAsync(Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Token);

        BasicGetResult? result = null;
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (result == null && DateTimeOffset.UtcNow < deadline)
        {
            result = await channel.BasicGetAsync(queueName, true, Token);
            if (result == null) await Task.Delay(100.Milliseconds(), Token);
        }

        result.ShouldNotBeNull("the published message never reached the queue");

        var headers = result.BasicProperties.Headers ?? new Dictionary<string, object?>();
        var replyUri = headers.TryGetValue(EnvelopeConstants.ReplyUriKey, out var raw) && raw is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

        if (expectReplyQueue)
        {
            transport.ReplyEndpoint().ShouldNotBeNull();
            responseQueues.ShouldHaveSingleItem();
            replyUri.ShouldNotBeNull();
            replyUri.ShouldContain(responseQueues[0].QueueName);
        }
        else
        {
            transport.ReplyEndpoint().ShouldBeNull();
            responseQueues.ShouldBeEmpty();
            replyUri.ShouldBeNull();
        }
    }
}

public record ReplyQueueEvent(Guid Id);
