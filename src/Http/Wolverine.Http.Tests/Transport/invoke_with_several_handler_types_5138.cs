using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Http.Transport;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

// GH-5138. The HTTP transport's /invoke route follows the same rules as IMessageBus.InvokeAsync() for a message type
// handled by several handler types: a request that asks for a reply runs the one handler type that returns it, and
// anything else runs every handler type. It used to answer 400, because the invoker of such a message type was not a
// single Executor.
public record SeveralHandlersQuestion5138(string Name);

public record SeveralHandlersAnswer5138(string Name);

public record SeveralHandlersNotice5138(string Name);

public static class SeveralHandlers5138
{
    public static readonly List<string> Received = [];

    public static TaskCompletionSource<bool> BothNoticed { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Record(string what)
    {
        lock (Received)
        {
            Received.Add(what);
            if (Received.Count(x => x.StartsWith("notice")) == 2) BothNoticed.TrySetResult(true);
        }
    }

    public static void Reset()
    {
        lock (Received) Received.Clear();
        BothNoticed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// Hidden from conventional discovery so they cannot leak into other hosts in this assembly
[WolverineIgnore]
public class AnsweringHandler5138
{
    public SeveralHandlersAnswer5138 Handle(SeveralHandlersQuestion5138 question)
    {
        SeveralHandlers5138.Record("answer");
        return new SeveralHandlersAnswer5138(question.Name);
    }

    public void Handle(SeveralHandlersNotice5138 notice) => SeveralHandlers5138.Record("notice:answering");
}

[WolverineIgnore]
public class AuditingHandler5138
{
    public void Handle(SeveralHandlersQuestion5138 question) => SeveralHandlers5138.Record("audit");

    public void Handle(SeveralHandlersNotice5138 notice) => SeveralHandlers5138.Record("notice:auditing");
}

public class invoke_with_several_handler_types_5138
{
    private const string InvokeUrl = "http://receiver.test/_wolverine/invoke";

    private static async Task<(WebApplication receiver, IHost sender)> bootstrap()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(AnsweringHandler5138))
                .IncludeType(typeof(AuditingHandler5138));
        });
        builder.Services.AddWolverineHttp();

        var receiver = builder.Build();
        receiver.MapWolverineHttpTransportEndpoints();
        await receiver.StartAsync(TestContext.Current.CancellationToken);

        var testServer = receiver.GetTestServer();

        var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<SeveralHandlersQuestion5138>().ToHttpEndpoint(InvokeUrl);
                opts.PublishMessage<SeveralHandlersNotice5138>().ToHttpEndpoint(InvokeUrl);
                opts.Services.AddWolverineHttp();
                opts.Services.AddHttpClient(InvokeUrl, c => c.BaseAddress = new Uri(InvokeUrl))
                    .ConfigurePrimaryHttpMessageHandler(() => testServer.CreateHandler());
            }).StartAsync(TestContext.Current.CancellationToken);

        return (receiver, sender);
    }

    [Fact]
    public async Task a_request_for_a_reply_runs_the_handler_type_that_returns_it()
    {
        SeveralHandlers5138.Reset();
        var (receiver, sender) = await bootstrap();
        await using var _ = receiver;
        using var __ = sender;

        var answer = await sender.MessageBus().InvokeAsync<SeveralHandlersAnswer5138>(
            new SeveralHandlersQuestion5138("Siuan"), TestContext.Current.CancellationToken);

        answer.Name.ShouldBe("Siuan");
        lock (SeveralHandlers5138.Received) SeveralHandlers5138.Received.ShouldBe(["answer"]);
    }

    [Fact]
    public async Task a_message_without_a_reply_runs_every_handler_type()
    {
        SeveralHandlers5138.Reset();
        var (receiver, sender) = await bootstrap();
        await using var _ = receiver;
        using var __ = sender;

        await sender.MessageBus().PublishAsync(new SeveralHandlersNotice5138("Leane"));

        await SeveralHandlers5138.BothNoticed.Task.WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        lock (SeveralHandlers5138.Received)
        {
            SeveralHandlers5138.Received.ShouldBe(["notice:answering", "notice:auditing"], ignoreOrder: true);
        }
    }
}
