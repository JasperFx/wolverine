using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5886. The pipeline's own end-of-chain flush sends what was enqueued after an earlier flush, while
/// <see cref="MultiFlushMode" /> keeps governing repeated explicit calls to FlushOutgoingMessagesAsync()
/// </summary>
public class flush_then_publish_5886
{
    private readonly MessageContext theContext;

    public flush_then_publish_5886()
    {
        var original = ObjectMother.Envelope();
        original.Id = Guid.NewGuid();

        theContext = new MessageContext(new MockWolverineRuntime());
        theContext.ReadEnvelope(original, InvocationCallback.Instance);
    }

    private async Task<Envelope> enqueueAsync()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Sender = Substitute.For<ISendingAgent>();

        // Wacky, but this adds it to the MessageContext
        await theContext.As<IEnvelopeTransaction>().PersistOutgoingAsync(envelope);
        return envelope;
    }

    [Fact]
    public async Task a_repeated_explicit_flush_under_only_once_still_sends_nothing()
    {
        theContext.MultiFlushMode.ShouldBe(MultiFlushMode.OnlyOnce);

        var first = await enqueueAsync();
        await theContext.FlushOutgoingMessagesAsync();
        await first.Sender!.Received(1).StoreAndForwardAsync(first);

        var second = await enqueueAsync();
        await theContext.FlushOutgoingMessagesAsync();

        await second.Sender!.DidNotReceive().StoreAndForwardAsync(second);
    }

    [Fact]
    public async Task a_repeated_explicit_flush_under_assert_on_multiples_still_throws()
    {
        theContext.MultiFlushMode = MultiFlushMode.AssertOnMultiples;

        await enqueueAsync();
        await theContext.FlushOutgoingMessagesAsync();

        var second = await enqueueAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => theContext.FlushOutgoingMessagesAsync());

        await second.Sender!.DidNotReceive().StoreAndForwardAsync(second);
    }

    [Theory]
    [InlineData(MultiFlushMode.OnlyOnce)]
    [InlineData(MultiFlushMode.AllowMultiples)]
    [InlineData(MultiFlushMode.AssertOnMultiples)]
    public async Task the_pipeline_flush_sends_what_was_enqueued_after_an_earlier_flush_exactly_once(
        MultiFlushMode mode)
    {
        theContext.MultiFlushMode = mode;

        var first = await enqueueAsync();
        await theContext.FlushOutgoingMessagesAsync();

        var second = await enqueueAsync();
        await theContext.FlushOutgoingMessagesFromPipelineAsync();

        await second.Sender!.Received(1).StoreAndForwardAsync(second);

        // Every flush clears what it sent, so nothing goes out twice
        await first.Sender!.Received(1).StoreAndForwardAsync(first);

        await theContext.FlushOutgoingMessagesFromPipelineAsync();
        await second.Sender!.Received(1).StoreAndForwardAsync(second);
        await first.Sender!.Received(1).StoreAndForwardAsync(first);
    }

    [Fact]
    public async Task a_handler_that_flushes_and_then_publishes_still_sends_the_later_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FlushThenPublishHandlers));
            }).StartAsync(TestContext.Current.CancellationToken);

        var tracked = await host.TrackActivity()
            .Timeout(10.Seconds())
            .SendMessageAndWaitAsync(new FlushThenPublish(Guid.NewGuid()));

        tracked.Executed.MessagesOf<FlushThenPublishTarget>().Select(x => x.Step).OrderBy(x => x)
            .ShouldBe(["after the flush", "before the flush"]);
    }
}

public record FlushThenPublish(Guid Id);

public record FlushThenPublishTarget(string Step);

[WolverineIgnore]
public static class FlushThenPublishHandlers
{
    public static async Task Handle(FlushThenPublish message, IMessageContext context)
    {
        await context.PublishAsync(new FlushThenPublishTarget("before the flush"));

        // What the commit of a session the handler saves itself does
        await ((MessageContext)context).FlushOutgoingMessagesAsync();

        await context.PublishAsync(new FlushThenPublishTarget("after the flush"));
    }

    public static void Handle(FlushThenPublishTarget message)
    {
    }
}
