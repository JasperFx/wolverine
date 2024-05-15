using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class interoperability_specs : RabbitMQContext, IAsyncLifetime
{
    private string theQueueName;
    private IHost _host;

    public async Task InitializeAsync()
    {
        theQueueName = RabbitTesting.NextQueueName();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.ListenToRabbitQueue(theQueueName)
                    .DefaultIncomingMessage<NumberMessage>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task send_raw_json_to_receiver()
    {
        var runtime = _host.GetRuntime();
        var json = "{\"Number\": \"55\"}";
        var envelope = new Envelope(new NumberMessage(55));
        var data = new SystemTextJsonSerializer(new JsonSerializerOptions()).Write(envelope);

        var transport = runtime.Options.RabbitMqTransport();

        var session = await _host.TrackActivity()
            .WaitForMessageToBeReceivedAt<NumberMessage>(_host)
            .ExecuteAndWaitAsync(m =>
        {
            using var channel = transport.SendingConnection.CreateModel();
            var props = channel.CreateBasicProperties();

            channel.BasicPublish(string.Empty, theQueueName, true, props, data);

            return Task.CompletedTask;
        });

        session.Received.SingleEnvelope<NumberMessage>()
            .ShouldNotBeNull();

        session.Executed.SingleMessage<NumberMessage>()
            .Number.ShouldBe(55);
    }
}

public record NumberMessage(int Number);

public static class NumberMessageHandler
{
    public static void Handle(NumberMessage message) => Console.WriteLine("Got number " + message.Number);
}