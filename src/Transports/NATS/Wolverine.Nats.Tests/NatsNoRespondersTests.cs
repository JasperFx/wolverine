using System.Diagnostics;
using System.Text;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Testcontainers.Nats;
using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// The NATS server answers a core publish that carries a reply subject but reaches no subscriber with an empty
/// "no responders" status message (503) on that reply subject. Wolverine's per-node reply subject could not tie
/// that answer to a request, so <c>InvokeAsync()</c> sat out its whole timeout. Each request now carries
/// its own reply token; the 503 completes the waiting call with a failure straight away.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsNoRespondersTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsNoRespondersTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task responder_that_may_only_publish_responses_still_answers()
    {
        // allow_responses: the responder may publish only to the reply subject of a request it received, so
        // the reply has to go to exactly that subject, per-request token included
        const string configPath = "/etc/nats/allow-responses.conf";
        await using var server = new NatsBuilder()
            .WithImage(NatsContainerFixture.NatsImage)
            .WithResourceMapping(Encoding.UTF8.GetBytes(
                """
                authorization {
                  users = [
                    { user: requester, password: requester-secret }
                    { user: responder, password: responder-secret, permissions: { subscribe: ">", allow_responses: true } }
                  ]
                }
                """), configPath)
            .WithCommand("--config", configPath)
            .Build();
        await server.StartAsync(TestContext.Current.CancellationToken);
        var url = server.GetConnectionString();
        var subject = $"allowresponses.{Guid.NewGuid():N}";

        using var responder = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "AllowResponsesResponder";
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NoResponderRequestHandler));
                opts.UseNats(url).WithCredentials("responder", "responder-secret");
                opts.ListenToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var requester = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "AllowResponsesRequester";
                opts.Discovery.DisableConventionalDiscovery();
                opts.UseNats(url).WithCredentials("requester", "requester-secret");
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<NoResponderRequest>().ToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var reply = await requester.MessageBus().InvokeAsync<NoResponderReply>(new NoResponderRequest("ping"),
            TestContext.Current.CancellationToken, 10.Seconds());

        reply.Text.ShouldBe("pong to ping");

        // The acknowledgement of an InvokeAsync() without a response type takes the same way back
        await requester.MessageBus().InvokeAsync(new NoResponderRequest("acknowledge"),
            TestContext.Current.CancellationToken, 10.Seconds());
    }

    [Fact]
    public async Task invoke_async_fails_fast_when_nobody_listens()
    {
        var subject = $"noresponders.{Guid.NewGuid():N}";
        using var host = await startRequesterAsync(subject);

        var stopwatch = Stopwatch.StartNew();
        var exception = await Should.ThrowAsync<WolverineRequestReplyException>(() =>
            host.MessageBus().InvokeAsync<NoResponderReply>(new NoResponderRequest("typed"),
                TestContext.Current.CancellationToken, 20.Seconds()));
        stopwatch.Stop();

        _output.WriteLine($"{stopwatch.Elapsed}: {exception.Message}");
        stopwatch.Elapsed.ShouldBeLessThan(5.Seconds());
        exception.Message.ShouldContain("no responders");
    }

    [Fact]
    public async Task invoke_async_without_a_response_fails_fast_when_nobody_listens()
    {
        var subject = $"noresponders.{Guid.NewGuid():N}";
        using var host = await startRequesterAsync(subject);

        var stopwatch = Stopwatch.StartNew();
        await Should.ThrowAsync<WolverineRequestReplyException>(() =>
            host.MessageBus().InvokeAsync(new NoResponderRequest("acknowledged"),
                TestContext.Current.CancellationToken, 20.Seconds()));
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(5.Seconds());
    }

    [Fact]
    public async Task request_reply_still_round_trips_with_a_shared_default_queue_group()
    {
        var subject = $"noresponders.{Guid.NewGuid():N}";

        using var responder = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NoRespondersResponder";
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NoResponderRequestHandler));
                opts.UseNats(nats =>
                {
                    nats.ConnectionString = _fixture.ConnectionString;
                    nats.DefaultQueueGroup = "shared-workers";
                });
                opts.ListenToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        using var requester = await startRequesterAsync(subject, "shared-workers");

        var reply = await requester.MessageBus().InvokeAsync<NoResponderReply>(new NoResponderRequest("ping"),
            TestContext.Current.CancellationToken, 20.Seconds());

        reply.Text.ShouldBe("pong to ping");
    }

    private async Task<IHost> startRequesterAsync(string subject, string? queueGroup = null)
    {
        return await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NoRespondersRequester";

                // A local handler would make InvokeAsync() run in process instead of going over NATS
                opts.Discovery.DisableConventionalDiscovery();

                opts.UseNats(nats =>
                {
                    nats.ConnectionString = _fixture.ConnectionString;
                    nats.DefaultQueueGroup = queueGroup;
                });

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<NoResponderRequest>().ToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }
}

public record NoResponderRequest(string Text);

public record NoResponderReply(string Text);

public static class NoResponderRequestHandler
{
    public static NoResponderReply Handle(NoResponderRequest request) => new($"pong to {request.Text}");
}
