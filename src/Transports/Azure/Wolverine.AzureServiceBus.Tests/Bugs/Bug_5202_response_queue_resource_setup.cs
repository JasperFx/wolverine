using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Bugs;

public class ResponseQueueResourceSetupTests5202 : IAsyncLifetime
{
    private readonly string _prefix = "gh5202_" + Guid.NewGuid().ToString("N");
    private IHost? _host;
    private bool _stopped;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceSetupAndRuntimeUseTheSameResponseQueue(bool useAsbControlQueue)
    {
        var ct = TestContext.Current.CancellationToken;
        var requestQueue = _prefix + ".requests";
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "gh5202";
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, _prefix);
                opts.Services.AddResourceSetupOnStartup();

                // The testing helper enables AutoProvision, which masks a queue missed by resource setup.
                var asb = opts.UseAzureServiceBusEmulator(Servers.AzureServiceBusConnectionString,
                        Servers.AzureServiceBusManagementConnectionString)
                    .SystemQueuePrefix(_prefix);
                if (useAsbControlQueue) asb.EnableWolverineControlQueues();

                opts.ListenToAzureServiceBusQueue(requestQueue).UseDurableInbox();
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5202RequestHandler));
            }).Build();

        var runtime = _host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var provisionalNode = runtime.Options.Durability.AssignedNodeNumber;
        var provisionedName = transport.PrefixSystemQueueName($"wolverine.response.gh5202.{provisionalNode}");
        transport.AutoProvision.ShouldBeFalse();

        // Exercise the actual resource-setup and Wolverine hosted services, not manual endpoint initialization.
        await _host.StartAsync(ct);

        var assignedNode = runtime.Options.Durability.AssignedNodeNumber;
        assignedNode.ShouldNotBe(provisionalNode);
        var node = (await runtime.Storage.Nodes.LoadAllNodesAsync(ct)).Single();
        node.NodeId.ShouldBe(runtime.Options.UniqueNodeId);
        node.AssignedNodeNumber.ShouldBe(assignedNode);

        var responses = transport.Queues
            .Where(x => x.EndpointName == AzureServiceBusTransport.ResponseEndpointName).ToArray();
        responses.Length.ShouldBe(1, "database node registration must not create a second response endpoint");
        var responseQueue = responses[0];
        responseQueue.QueueName.ShouldBe(provisionedName);
        transport.AutoProvision.ShouldBeFalse();

        var admin = new ServiceBusAdministrationClient(Servers.AzureServiceBusManagementConnectionString);
        (await admin.QueueExistsAsync(provisionedName, ct)).Value.ShouldBeTrue();

        var reply = await _host.MessageBus().EndpointFor(new Uri($"asb://queue/{requestQueue}"))
            .InvokeAsync<Gh5202Response>(new Gh5202Request("request"), ct, TimeSpan.FromSeconds(30));
        reply.Value.ShouldBe("request");
        if (!useAsbControlQueue)
        {
            // An ASB control queue is also eligible for replies. Without it, this must use the response queue.
            reply.ReplyUri.ShouldBe(responseQueue.Uri.ToString());
        }

        node.ControlUri.ShouldNotBeNull();
        node.ControlUri.Scheme.ShouldBe(useAsbControlQueue ? "asb" : "dbcontrol");
        var controlReply = await _host.MessageBus().EndpointFor(node.ControlUri)
            .InvokeAsync<Gh5202Response>(new Gh5202Request("control"), ct, TimeSpan.FromSeconds(30));
        controlReply.Value.ShouldBe("control");

        // Stop competing listeners before proving that the provisioned queue accepts a real SDK round trip.
        await _host.StopAsync(ct);
        _stopped = true;
        await using var client = new ServiceBusClient(Servers.AzureServiceBusConnectionString);
        await using var sender = client.CreateSender(provisionedName);
        await using var receiver = client.CreateReceiver(provisionedName);
        var messageId = Guid.NewGuid().ToString("N");
        await sender.SendMessageAsync(new ServiceBusMessage("gh5202") { MessageId = messageId }, ct);
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), ct);
        received.ShouldNotBeNull();
        received.MessageId.ShouldBe(messageId);
        await receiver.CompleteMessageAsync(received, ct);
    }

    public async ValueTask DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (_host != null)
            {
                try
                {
                    if (!_stopped) await _host.StopAsync(cleanup.Token);
                }
                finally
                {
                    _host.Dispose();
                }
            }
        }
        finally
        {
            try
            {
                // Also runs when StartAsync fails; never delete another test's or developer's resources.
                var admin = new ServiceBusAdministrationClient(Servers.AzureServiceBusManagementConnectionString);
                await foreach (var queue in admin.GetQueuesAsync(cleanup.Token))
                {
                    if (queue.Name.StartsWith(_prefix + ".", StringComparison.Ordinal))
                    {
                        await admin.DeleteQueueAsync(queue.Name, cleanup.Token);
                    }
                }
            }
            finally
            {
                await using var connection = new NpgsqlConnection(Servers.PostgresConnectionString);
                await connection.OpenAsync(cleanup.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"DROP SCHEMA IF EXISTS \"{_prefix}\" CASCADE";
                await command.ExecuteNonQueryAsync(cleanup.Token);
            }
        }
    }
}

public record Gh5202Request(string Value);
public record Gh5202Response(string Value, string? ReplyUri);

public static class Gh5202RequestHandler
{
    public static Gh5202Response Handle(Gh5202Request request, IMessageContext context) =>
        new(request.Value, context.Envelope?.ReplyUri?.ToString());
}
