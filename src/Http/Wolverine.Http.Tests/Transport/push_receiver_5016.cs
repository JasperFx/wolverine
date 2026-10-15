using System.Text;
using System.Text.Json;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Http.Transport;
using Wolverine.Http.Transport.Push;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.Http.Tests.Transport;

/// <summary>
///     GH-5016. The transport-agnostic push receiver: a broker pushes a message over HTTP, a pluggable mapper turns
///     it into an envelope, the handler runs inline inside the request, outgoing messages are sent before the
///     response, and the status code acks or asks for redelivery
/// </summary>
public class push_receiver_5016
{
    private static async Task<IAlbaHost> hostWith(Action<WolverineOptions>? configure = null,
        Action<PushReceiverOptions>? push = null)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PushedMessageHandler));
            opts.Services.AddSingleton<PushRecorder>();

            opts.OnException<RequeueException>().Requeue();
            opts.OnException<ScheduleException>().ScheduleRetry(1.Minutes());
            opts.OnException<DeadLetterException>().MoveToErrorQueue();
            opts.OnException<FlakyException>().RetryTimes(2);

            opts.Transports.GetOrCreate<RecordingTransport>();
            opts.PublishMessage<PushCascade>().To(RecordingTransport.OutUri);

            configure?.Invoke(opts);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            #region sample_map_wolverine_push_receiver

            // The mapper is the transport's: it turns the request the broker pushes into a Wolverine envelope.
            // Protect this endpoint before you deploy it, for example with the push subscription's OIDC token
            app.MapWolverinePushReceiver("/_wolverine/push/orders", new FakePubsubPushMapper(), push);

            #endregion

            app.MapWolverinePushReceiver<WolverineEnvelopePushMapper>("/_wolverine/push/envelopes");
        });
    }

    private static Task<IScenarioResult> push(IAlbaHost host, string name, Guid? id = null, int status = 200)
    {
        return host.Scenario(x =>
        {
            x.Post.Json(FakePubsubPushMapper.Body(name, id ?? Guid.NewGuid())).ToUrl("/_wolverine/push/orders");
            x.StatusCodeShouldBe(status);
        });
    }

    private static PushRecorder recorderFor(IAlbaHost host) => host.Services.GetRequiredService<PushRecorder>();

    private static RecordingEndpoint outFor(IAlbaHost host) => host.Services.GetRequiredService<IWolverineRuntime>()
        .Options.Transports.GetOrCreate<RecordingTransport>().Endpoints["out"];

    [Fact]
    public async Task handles_the_pushed_message_inline_and_acks_it()
    {
        await using var host = await hostWith();

        await push(host, "one");

        // Handled before the response was written, not handed to a queue
        recorderFor(host).Handled.ShouldBe(["one"]);
    }

    [Fact]
    public async Task sends_the_cascaded_messages_before_responding()
    {
        await using var host = await hostWith();

        await push(host, "cascading");

        outFor(host).Sent.Select(x => x.Message).OfType<PushCascade>().Single().Name.ShouldBe("cascading");
    }

    [Fact]
    public async Task a_request_without_a_message_is_a_bad_request()
    {
        await using var host = await hostWith();

        await host.Scenario(x =>
        {
            x.Post.Json(new { subscription = "nothing here" }).ToUrl("/_wolverine/push/orders");
            x.StatusCodeShouldBe(400);
        });

        recorderFor(host).Handled.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_requeue_asks_the_broker_to_redeliver()
    {
        await using var host = await hostWith();

        await push(host, "requeue", status: 500);

        // Nothing it tried to send goes out for a message the broker is about to deliver again
        outFor(host).Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_scheduled_retry_is_left_to_the_broker()
    {
        await using var host = await hostWith();

        await push(host, "schedule", status: 500);
    }

    [Fact]
    public async Task moving_to_the_error_queue_acks_the_message()
    {
        await using var host = await hostWith();

        await push(host, "dead-letter", status: 200);

        recorderFor(host).Handled.ShouldBe(["dead-letter"]);
        outFor(host).Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_inline_retry_runs_inside_the_request_and_sends_once()
    {
        await using var host = await hostWith();

        await push(host, "flaky");

        recorderFor(host).Handled.ShouldBe(["flaky", "flaky"]);
        outFor(host).Sent.Select(x => x.Message).OfType<PushCascade>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task the_retry_status_code_is_configurable()
    {
        await using var host = await hostWith(push: o => o.RetryStatusCode = StatusCodes.Status503ServiceUnavailable);

        await push(host, "requeue", status: 503);
    }

    [Fact]
    public async Task receives_a_whole_wolverine_envelope()
    {
        await using var host = await hostWith();

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(PushedMessage).ToMessageTypeName(),
            ContentType = "application/json",
            Data = JsonSerializer.SerializeToUtf8Bytes(new PushedMessage("native"))
        };

        await host.Scenario(x =>
        {
            x.Post.ByteArray(EnvelopeSerializer.Serialize(envelope)).ToUrl("/_wolverine/push/envelopes")
                .ContentType(HttpTransport.EnvelopeContentType);
            x.StatusCodeShouldBeOk();
        });

        recorderFor(host).Handled.ShouldBe(["native"]);
    }

    [Fact]
    public async Task the_envelope_mapper_rejects_anything_else()
    {
        await using var host = await hostWith();

        await host.Scenario(x =>
        {
            x.Post.ByteArray([1, 2, 3]).ToUrl("/_wolverine/push/envelopes").ContentType(HttpTransport.EnvelopeContentType);
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public void pattern_cannot_be_empty()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine();
        var app = builder.Build();

        Should.Throw<ArgumentNullException>(() => app.MapWolverinePushReceiver("", new FakePubsubPushMapper()));
    }

    private static void persistInPostgres(WolverineOptions opts, string schema)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
        opts.Services.AddResourceSetupOnStartup();
    }

    [Fact]
    public async Task deduplicates_a_redelivery_with_the_inbox()
    {
        await using var host = await hostWith(opts => persistInPostgres(opts, "push_dedupe"),
            o => o.DeduplicateWithInbox = true);
        await host.ResetResourceState(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await push(host, "once", id);
        await push(host, "once", id);

        // Acked both times, handled once
        recorderFor(host).Handled.ShouldBe(["once"]);
    }

    [Fact]
    public async Task a_message_that_will_be_redelivered_is_not_recorded_as_handled()
    {
        await using var host = await hostWith(opts => persistInPostgres(opts, "push_dedupe"),
            o => o.DeduplicateWithInbox = true);
        await host.ResetResourceState(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid();
        await push(host, "requeue", id, status: 500);
        await push(host, "requeue", id, status: 500);

        recorderFor(host).Handled.ShouldBe(["requeue", "requeue"]);
    }

    [Fact]
    public async Task without_deduplication_a_redelivery_is_handled_again()
    {
        await using var host = await hostWith();

        var id = Guid.NewGuid();
        await push(host, "twice", id);
        await push(host, "twice", id);

        recorderFor(host).Handled.ShouldBe(["twice", "twice"]);
    }

    [Fact]
    public async Task under_serverless_with_outbox_a_failed_send_is_left_in_the_outbox()
    {
        await using var host = await hostWith(opts =>
        {
            opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
            persistInPostgres(opts, "push_outbox");
        });
        await host.ResetResourceState(TestContext.Current.CancellationToken);

        var endpoint = outFor(host);
        endpoint.FailSends = true;

        // The message itself was handled, so it is acked
        await push(host, "unsent");

        var outgoing = await host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.AllOutgoingAsync();
        var row = outgoing.Single();
        row.MessageType.ShouldBe(typeof(PushCascade).ToMessageTypeName());

        // Any node's to recover, on the next durability pass
        row.OwnerId.ShouldBe(0);
    }

    [Fact]
    public async Task under_serverless_with_outbox_sends_inline_and_leaves_nothing_behind()
    {
        await using var host = await hostWith(opts =>
        {
            opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
            persistInPostgres(opts, "push_outbox");
        });
        await host.ResetResourceState(TestContext.Current.CancellationToken);

        await push(host, "sent");

        outFor(host).Sent.Select(x => x.Message).OfType<PushCascade>().Single().Name.ShouldBe("sent");

        var outgoing = await host.Services.GetRequiredService<IWolverineRuntime>().Storage.Admin.AllOutgoingAsync();
        outgoing.ShouldBeEmpty();
    }
}

public record PushedMessage(string Name);

public record PushCascade(string Name);

public class RequeueException : Exception;

public class ScheduleException : Exception;

public class DeadLetterException : Exception;

public class FlakyException : Exception;

public class PushRecorder
{
    public List<string> Handled { get; } = new();
}

public static class PushedMessageHandler
{
    public static PushCascade Handle(PushedMessage message, Envelope envelope, PushRecorder recorder)
    {
        recorder.Handled.Add(message.Name);

        switch (message.Name)
        {
            case "requeue":
                throw new RequeueException();
            case "schedule":
                throw new ScheduleException();
            case "dead-letter":
                throw new DeadLetterException();
            case "flaky" when envelope.Attempts < 2:
                throw new FlakyException();
        }

        return new PushCascade(message.Name);
    }
}

/// <summary>
///     Shaped like a Google Cloud Pub/Sub push request: the message's data is base64 encoded, and its attributes
///     carry the Wolverine message type
/// </summary>
public class FakePubsubPushMapper : IPushEnvelopeMapper
{
    public static object Body(string name, Guid id) => new
    {
        message = new
        {
            messageId = id.ToString(),
            data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PushedMessage(name))),
            attributes = new Dictionary<string, string> { ["message-type"] = typeof(PushedMessage).ToMessageTypeName() }
        },
        subscription = "projects/demo/subscriptions/orders"
    };

    public async ValueTask<Envelope?> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellation);
        if (!document.RootElement.TryGetProperty("message", out var message)) return null;

        return new Envelope
        {
            Id = Guid.Parse(message.GetProperty("messageId").GetString()!),
            Data = Convert.FromBase64String(message.GetProperty("data").GetString()!),
            MessageType = message.GetProperty("attributes").GetProperty("message-type").GetString(),
            ContentType = "application/json"
        };
    }
}

/// <summary>
///     A broker-free transport that records what was sent to it, and can be told to fail
/// </summary>
public class RecordingTransport : TransportBase<RecordingEndpoint>
{
    public static readonly Uri OutUri = new("recording://out");

    public RecordingTransport() : base("recording", "Recording", [])
    {
        Endpoints = new LightweightCache<string, RecordingEndpoint>(name => new RecordingEndpoint(name));
    }

    public new LightweightCache<string, RecordingEndpoint> Endpoints { get; }

    public override Endpoint? ReplyEndpoint() => null;

    protected override IEnumerable<RecordingEndpoint> endpoints() => Endpoints;

    protected override RecordingEndpoint findEndpointByUri(Uri uri) => Endpoints[uri.Host];
}

public class RecordingEndpoint : Endpoint, ISender
{
    public RecordingEndpoint(string name) : base(new Uri($"recording://{name}"), EndpointRole.Application)
    {
        EndpointName = name;
    }

    public List<Envelope> Sent { get; } = new();
    public bool FailSends { get; set; }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => Uri;
    public Task<bool> PingAsync() => Task.FromResult(true);

    public ValueTask SendAsync(Envelope envelope)
    {
        if (FailSends) throw new InvalidOperationException("The broker is unavailable");

        Sent.Add(envelope);
        return ValueTask.CompletedTask;
    }

    protected override bool supportsMode(EndpointMode mode) => mode != EndpointMode.NativeAck;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        => throw new NotSupportedException();

    protected override ISender CreateSender(IWolverineRuntime runtime) => this;
}
