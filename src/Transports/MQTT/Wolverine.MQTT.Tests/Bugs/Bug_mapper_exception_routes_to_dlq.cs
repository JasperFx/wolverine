using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Client;
using Shouldly;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace Wolverine.MQTT.Tests.Bugs;

[Collection("mosquitto")]
public class Bug_mapper_exception_routes_to_dlq : IAsyncLifetime
{
    private readonly string _topic = "mapper-explosion/" + Guid.NewGuid().ToString("N");
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseMqtt(builder =>
                {
                    builder.WithClientOptions(client =>
                        client.WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port));
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "wolverine");

                opts.ListenToMqttTopic(_topic)
                    .UseInterop(new AlwaysThrowingMqttMapper())
                    .UseDurableInbox();
            }).StartAsync();

        // Clear prior dead-letter rows so the test is deterministic.
        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task unmappable_message_is_persisted_to_wolverine_dead_letters()
    {
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(MosquittoContainerFixture.Host, MosquittoContainerFixture.Port)
            .Build();

        await client.ConnectAsync(options);
        await client.PublishStringAsync(_topic, "hello");

        var storage = _host.GetRuntime().Storage;

        var attempts = 0;
        while (attempts < 40)
        {
            var results = await storage.DeadLetters.QueryAsync(
                new DeadLetterEnvelopeQuery(TimeRange.AllTime()),
                CancellationToken.None);

            if (results.Envelopes.Count > 0)
            {
                results.Envelopes[0].ExceptionType.ShouldContain(nameof(InvalidOperationException));
                return;
            }

            attempts++;
            await Task.Delay(250.Milliseconds());
        }

        throw new Exception(
            "Expected an entry in wolverine_dead_letters after a mapper exception, but none was persisted (silent loss).");
    }
}

internal class AlwaysThrowingMqttMapper : IMqttEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, MqttApplicationMessage outgoing)
    {
    }

    public void MapIncomingToEnvelope(Envelope envelope, MqttApplicationMessage incoming)
    {
        throw new InvalidOperationException("simulated mapper failure");
    }
}
