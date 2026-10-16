using System.Net;
using System.Text;
using System.Text.Json;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Http.Transport.Push;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Util;

namespace Wolverine.Http.Tests.Transport;

/// <summary>
///     GH-5049. Under <see cref="PushReceiverOptions.DeduplicateWithInbox" /> the push receiver claims the message id
///     in the inbox before running the handler, so a redelivery that arrives while the first delivery is still
///     running (a handler slower than the push subscription's ack deadline) is not run a second time
/// </summary>
public class push_receiver_inbox_claim_5049
{
    private const string Route = "/_wolverine/push/claims";

    private static async Task<IAlbaHost> hostWith(Action<WolverineOptions>? configure = null, string schema = "push_claim")
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ClaimedPushHandler));
            opts.Services.AddSingleton<ClaimedPushGate>();

            opts.OnException<ClaimedPushRequeueException>().Requeue();
            opts.OnException<ClaimedPushDeadLetterException>().MoveToErrorQueue();

            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
            opts.Services.AddResourceSetupOnStartup();

            configure?.Invoke(opts);
        });

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverinePushReceiver(Route, new ClaimedPushMapper(), o =>
            {
                o.AllowAnonymous();
                o.DeduplicateWithInbox = true;
            });
        });

        await host.ResetResourceState(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<HttpStatusCode> push(HttpClient client, Guid id, string name)
    {
        var body = new StringContent(JsonSerializer.Serialize(new ClaimedPush(name) { Id = id }), Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync(Route, body, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static ClaimedPushGate gateFor(IAlbaHost host) => host.Services.GetRequiredService<ClaimedPushGate>();

    private static IWolverineRuntime runtimeFor(IAlbaHost host) => host.Services.GetRequiredService<IWolverineRuntime>();

    [Fact]
    public async Task a_redelivery_while_the_first_delivery_is_still_running_gets_the_retry_status()
    {
        await using var host = await hostWith();
        var client = host.Server.CreateClient();
        var gate = gateFor(host);

        var id = Guid.NewGuid();
        var first = push(client, id, "slow");
        await gate.Started.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        // The ack deadline passed and the broker delivered it again
        (await push(client, id, "slow")).ShouldBe(HttpStatusCode.InternalServerError);
        gate.Runs.ShouldBe(1);

        gate.Release.SetResult();
        (await first).ShouldBe(HttpStatusCode.OK);

        // And once it is handled, a later redelivery is acked without running it
        (await push(client, id, "slow")).ShouldBe(HttpStatusCode.OK);
        gate.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task the_claim_is_an_incoming_row_owned_by_this_node_until_the_message_is_handled()
    {
        await using var host = await hostWith();
        var client = host.Server.CreateClient();
        var gate = gateFor(host);
        var runtime = runtimeFor(host);

        var id = Guid.NewGuid();
        var first = push(client, id, "slow");
        await gate.Started.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var claim = (await runtime.Storage.Admin.AllIncomingAsync()).Single(x => x.Id == id);
        claim.Status.ShouldBe(EnvelopeStatus.Incoming);
        claim.OwnerId.ShouldBe(runtime.Options.Durability.AssignedNodeNumber);

        gate.Release.SetResult();
        (await first).ShouldBe(HttpStatusCode.OK);

        var handled = (await runtime.Storage.Admin.AllIncomingAsync()).Single(x => x.Id == id);
        handled.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task a_retry_releases_the_claim_so_the_redelivery_runs()
    {
        await using var host = await hostWith();
        var client = host.Server.CreateClient();
        var gate = gateFor(host);

        var id = Guid.NewGuid();
        (await push(client, id, "requeue")).ShouldBe(HttpStatusCode.InternalServerError);
        (await runtimeFor(host).Storage.Admin.AllIncomingAsync()).ShouldNotContain(x => x.Id == id);

        (await push(client, id, "requeue")).ShouldBe(HttpStatusCode.InternalServerError);
        gate.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task a_dead_lettered_message_is_recorded_as_handled()
    {
        await using var host = await hostWith();
        var client = host.Server.CreateClient();
        var gate = gateFor(host);

        var id = Guid.NewGuid();
        (await push(client, id, "dead-letter")).ShouldBe(HttpStatusCode.OK);

        // Moving it to dead letter storage deleted the claim, so it is recorded as handled on its own
        (await push(client, id, "dead-letter")).ShouldBe(HttpStatusCode.OK);
        gate.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task under_inbox_partitioning_a_handled_message_is_still_a_duplicate()
    {
        // A Handled row sits in another partition, so it does not collide with a new claim and has to be looked for
        await using var host = await hostWith(opts => opts.Durability.EnableInboxPartitioning = true,
            "push_claim_partitioned");
        var client = host.Server.CreateClient();

        var id = Guid.NewGuid();
        (await push(client, id, "fast")).ShouldBe(HttpStatusCode.OK);
        (await push(client, id, "fast")).ShouldBe(HttpStatusCode.OK);

        gateFor(host).Runs.ShouldBe(1);
    }

    [Fact]
    public async Task a_durability_pass_does_not_run_a_message_a_push_request_is_still_handling()
    {
        await using var host = await hostWith(opts => opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox);
        var client = host.Server.CreateClient();
        var gate = gateFor(host);

        var id = Guid.NewGuid();
        var first = push(client, id, "slow");
        await gate.Started.Task.WaitAsync(10.Seconds(), TestContext.Current.CancellationToken);

        var report = await host.RunDurabilityPassAsync(maxDuration: 10.Seconds(), TestContext.Current.CancellationToken);
        report.IncomingRecovered.ShouldBe(0);
        gate.Runs.ShouldBe(1);

        gate.Release.SetResult();
        (await first).ShouldBe(HttpStatusCode.OK);
        gate.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task why_the_claim_is_not_owned_by_any_node()
    {
        await using var host = await hostWith(opts => opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox);
        var runtime = runtimeFor(host);

        // The same row owned by any node is recovered, and so run, by the very next durability pass
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(ClaimedPush).ToMessageTypeName(),
            ContentType = "application/json",
            Data = JsonSerializer.SerializeToUtf8Bytes(new ClaimedPush("fast")),
            Destination = new Uri("http://localhost" + Route),
            Status = EnvelopeStatus.Incoming,
            OwnerId = 0
        };
        await runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        var report = await host.RunDurabilityPassAsync(maxDuration: 10.Seconds(), TestContext.Current.CancellationToken);
        report.IncomingRecovered.ShouldBe(1);
        gateFor(host).Runs.ShouldBe(1);
    }
}

public record ClaimedPush(string Name)
{
    public Guid Id { get; init; }
}

public class ClaimedPushRequeueException : Exception;

public class ClaimedPushDeadLetterException : Exception;

public class ClaimedPushGate
{
    private int _runs;

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Runs => _runs;

    public int Run() => Interlocked.Increment(ref _runs);
}

public static class ClaimedPushHandler
{
    public static async Task Handle(ClaimedPush message, ClaimedPushGate gate)
    {
        var run = gate.Run();

        switch (message.Name)
        {
            // Only the first run is held, so a second concurrent run would finish at once
            case "slow" when run == 1:
                gate.Started.SetResult();
                await gate.Release.Task.WaitAsync(30.Seconds());
                break;
            case "requeue":
                throw new ClaimedPushRequeueException();
            case "dead-letter":
                throw new ClaimedPushDeadLetterException();
        }
    }
}

/// <summary>
///     Reads the message straight out of the JSON body, with the broker's message id in it
/// </summary>
public class ClaimedPushMapper : IPushEnvelopeMapper
{
    public async ValueTask<IReadOnlyList<Envelope>> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        using var stream = new MemoryStream();
        await context.Request.Body.CopyToAsync(stream, cancellation);
        var data = stream.ToArray();

        var message = JsonSerializer.Deserialize<ClaimedPush>(data)!;

        return
        [
            new Envelope
            {
                Id = message.Id,
                Data = data,
                MessageType = typeof(ClaimedPush).ToMessageTypeName(),
                ContentType = "application/json"
            }
        ];
    }
}
