using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5016. A Wolverine.HTTP endpoint under DurabilityMode.ServerlessWithOutbox that has NO transactional
/// middleware (no Marten session, no DbContext) still has its outgoing messages flushed by the chain's plain
/// FlushOutgoingMessages postprocessor. Those messages must be sent before the response is written, and a
/// message whose send fails must be left in the outbox as owned by any node (0) so a durability pass can
/// recover it. The sends go over TCP, which uses a real DurableSendingAgent, rather than the stub transport,
/// which sends synchronously and would hide the background send.
/// </summary>
public class serverless_with_outbox_http_endpoints_5016 : IAsyncDisposable
{
    private const string SchemaName = "gh5016_http";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly int thePort = PortFinder.GetAvailablePort();
    private IAlbaHost? theFunction;
    private IHost? theReceiver;

    public async ValueTask DisposeAsync()
    {
        if (theFunction != null) await theFunction.DisposeAsync();

        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }
    }

    private async Task startTheFunctionAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

            // So a pass recovers an unsent row right away instead of after the 60 second default
            opts.Durability.OutboxStaleTime = 100.Milliseconds();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(serverless_with_outbox_http_endpoints_5016).Assembly);

            // A plain PostgreSQL message store: no Marten, no EF Core, so nothing commits a transaction
            // and the HTTP chain's own flush is the only thing that sends
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

            opts.PublishMessage<Gh5016HttpCascaded>().ToPort(thePort);

            opts.Services.AddResourceSetupOnStartup();
        });

        builder.Services.AddWolverineHttp();

        theFunction = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-5016 serverless endpoints",
                    type => type != typeof(Gh5016ServerlessHttpEndpoints)));
        }));

        await theFunction.ResetResourceState();
    }

    private async Task startTheReceiverAsync()
    {
        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Gh5016HttpCascadedHandler));
                opts.ListenAtPort(thePort);
            }).StartAsync(Token);
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);

        var rows = new Dictionary<Guid, int>();
        await using var reader = await conn
            .CreateCommand($"select id, owner_id from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows[await reader.GetFieldValueAsync<Guid>(0, Token)] = await reader.GetFieldValueAsync<int>(1, Token);
        }

        return rows;
    }

    private static async Task shouldBeReceivedAsync(Guid id)
    {
        var timeout = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (!Gh5016HttpCascadedHandler.Received.ContainsKey(id))
        {
            if (DateTimeOffset.UtcNow > timeout)
            {
                throw new TimeoutException($"Message {id} never reached the receiver");
            }

            await Task.Delay(50, Token);
        }
    }

    private async Task postAsync(string url, Guid id)
    {
        await theFunction!.Scenario(x =>
        {
            x.Post.Json(new Gh5016HttpCommand(id)).ToUrl(url);
            x.StatusCodeShouldBeOk();
        });
    }

    [Theory]
    [InlineData("/gh5016/cascade")]
    [InlineData("/gh5016/publish")]
    public async Task outgoing_messages_are_sent_before_the_response(string url)
    {
        await startTheReceiverAsync();
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await postAsync(url, id);

        // Nothing can be trusted to run after the response in a serverless function, so the message was
        // already accepted by the receiver and its outbox row deleted by the time the response came back
        (await outboxRowsAsync()).ShouldBeEmpty();
        await shouldBeReceivedAsync(id);
    }

    [Theory]
    [InlineData("/gh5016/cascade")]
    [InlineData("/gh5016/publish")]
    public async Task a_failed_send_is_left_for_a_durability_pass_to_recover(string url)
    {
        // Nothing is listening on the port yet, so the send fails
        await startTheFunctionAsync();

        var id = Guid.NewGuid();
        await postAsync(url, id);

        // Owned by any node, because there is no node record under ServerlessWithOutbox and a pass only
        // recovers rows owned by any node
        var rows = await outboxRowsAsync();
        rows.Count.ShouldBe(1);
        rows.Values.Single().ShouldBe(TransportConstants.AnyNode);

        // The receiver comes up later, and the next pass delivers the message
        await startTheReceiverAsync();
        await Task.Delay(200, Token);

        await theFunction!.RunDurabilityPassAsync(token: Token);

        await shouldBeReceivedAsync(id);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}

public record Gh5016HttpCommand(Guid Id);

public record Gh5016HttpCascaded(Guid Id);

public static class Gh5016ServerlessHttpEndpoints
{
    // No transactional middleware of any kind: the cascaded message goes out through the chain's
    // plain FlushOutgoingMessages postprocessor
    [WolverinePost("/gh5016/cascade")]
    public static (string, Gh5016HttpCascaded) Cascade(Gh5016HttpCommand command)
        => ("ok", new Gh5016HttpCascaded(command.Id));

    [WolverinePost("/gh5016/publish")]
    public static async Task<string> Publish(Gh5016HttpCommand command, IMessageBus bus)
    {
        await bus.PublishAsync(new Gh5016HttpCascaded(command.Id));
        return "ok";
    }
}

public static class Gh5016HttpCascadedHandler
{
    public static readonly ConcurrentDictionary<Guid, bool> Received = new();

    public static void Handle(Gh5016HttpCascaded message) => Received[message.Id] = true;
}
