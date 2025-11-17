using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.SharedMemory;
using Wolverine.Util;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class moving_unknown_message_type_to_dlq : IAsyncLifetime
{
    private IHost _sender;
    private IHost _receiver;

    public static async Task TestSample()
    {
        #region sample_unknown_messages_go_to_dead_letter_queue

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var connectionString = builder.Configuration.GetConnectionString("rabbit");
            opts.UseRabbitMq(connectionString).UseConventionalRouting();

            // All unknown message types received should be placed into 
            // the proper dead letter queue mechanism
            opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
        });

        #endregion
    }

    public async Task InitializeAsync()
    {
        await SharedMemoryQueueManager.ClearAllAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
                
                opts.PublishMessage<ToDurable>().ToRabbitQueue("durable");
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().DisableDeadLetterQueueing();

                opts.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "missing");

                // Forcing this to use the database backed DLQ
                opts.ListenToRabbitQueue("durable").UseDurableInbox();
            }).StartAsync();

        // Empty it out before you do anything here
        await _receiver.RebuildAllEnvelopeStorageAsync();
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }

    [Fact]
    public async Task send_message_with_no_handler_to_durable()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(1.Minutes())
            .IncludeExternalTransports()
            .SendMessageAndWaitAsync(new ToDurable("One"));

        var records = tracked.NoHandlers.Envelopes();
        records.Any(x => x.MessageType == typeof(ToDurable).ToMessageTypeName()).ShouldBeTrue();

        var storage = _receiver.GetRuntime().Storage;
        var deadLetters = await storage.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(TimeRange.AllTime()),
            CancellationToken.None);

        var envelope = deadLetters.Envelopes.Single();
        envelope.MessageType.ShouldBe(typeof(ToDurable).ToMessageTypeName());
        envelope.ExceptionType.ShouldBe("Wolverine.Runtime.Interop.UnknownMessageTypeNameException");
        envelope.ExceptionMessage.ShouldBe("Unknown message type: 'Wolverine.RabbitMQ.Tests.ToDurable'");
        
    }
}

public record ToInline(string Name);

public record ToBuffered(string Name);

public record ToDurable(string Name);
