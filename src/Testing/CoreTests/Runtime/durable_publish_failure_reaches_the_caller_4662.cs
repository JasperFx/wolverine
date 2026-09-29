using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-4662. <c>IMessageBus.PublishAsync</c> / <c>ScheduleAsync</c> from outside a handler and outside any
/// transaction, to a local queue made durable by <c>UseDurableLocalQueues()</c>, while the message store
/// is unreachable: the call returned <b>without an error</b>, four attempts were made in about 400 ms, the
/// envelope was discarded with a single line at <b>Information</b>, and the message was never handled —
/// not even after the database came back, because no inbox row was ever written.
///
/// <para>
/// The cause was the shape of the retry, not the retry itself. <c>RetryBlock.PostAsync</c> runs one
/// attempt inline and posts the rest to its own worker, so the task the caller awaits completes
/// <i>successfully</i> the moment the first attempt fails. That is the right shape once an envelope is
/// durable somewhere and something else can recover it. It is the wrong shape for the write that makes it
/// durable in the first place, because there is no other copy — no broker delivery, no inbox row — to
/// fall back on.
/// </para>
///
/// <para>
/// So the attempt budget is kept and awaited inline, and the last exception reaches the caller, which is
/// what the EF Core and database outbox paths already did for the same outage.
/// </para>
/// </summary>
public class durable_publish_failure_reaches_the_caller_4662
{
    /// <summary>
    /// A real store in every respect but the inbox, so the host boots exactly as it normally does and the
    /// only thing under test is what happens to a write that cannot land. Re-listing IMessageStore
    /// re-maps the interface to the <c>new</c> Inbox below.
    /// </summary>
    private class StoreWithAnUnreachableInbox : NullMessageStore, IMessageStore
    {
        public new IMessageInbox Inbox { get; } = Substitute.For<IMessageInbox>();
    }

    private static IHost hostWith(IMessageStore store)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(durable_publish_failure_reaches_the_caller_4662).Assembly;
                opts.Services.AddSingleton(store);
                opts.Policies.UseDurableLocalQueues();
                opts.PublishMessage<StrandedMessage>().ToLocalQueue("stranded");
            }).Start();
    }

    private static (IHost host, IMessageInbox inbox) unreachableStore()
    {
        var store = new StoreWithAnUnreachableInbox();

        store.Inbox.StoreIncomingAsync(Arg.Any<Envelope>())
            .Throws(new TimeoutException("the message store is unreachable"));
        store.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("the message store is unreachable"));

        return (hostWith(store), store.Inbox);
    }

    [Fact]
    public async Task publish_to_a_durable_local_queue_throws_when_the_store_is_unreachable()
    {
        var (host, _) = unreachableStore();
        using var _host = host;

        // Before the fix this returned successfully and the message was silently gone.
        await Should.ThrowAsync<TimeoutException>(() =>
            host.MessageBus().PublishAsync(new StrandedMessage("one")).AsTask());
    }

    [Fact]
    public async Task schedule_to_a_durable_local_queue_throws_when_the_store_is_unreachable()
    {
        var (host, _) = unreachableStore();
        using var _host = host;

        await Should.ThrowAsync<TimeoutException>(() =>
            host.MessageBus().ScheduleAsync(new StrandedMessage("two"), 1.Seconds()).AsTask());
    }

    [Fact]
    public async Task the_bounded_retry_budget_is_still_spent_before_giving_up()
    {
        // A transient blip must still cost the caller latency rather than an exception: the failure only
        // surfaces once the whole 50/100/250ms budget the RetryBlock used to spend is exhausted.
        var (host, inbox) = unreachableStore();
        using var _host = host;

        var clock = Stopwatch.StartNew();
        await Should.ThrowAsync<TimeoutException>(() =>
            host.MessageBus().PublishAsync(new StrandedMessage("three")).AsTask());
        clock.Stop();

        clock.Elapsed.ShouldBeGreaterThan(350.Milliseconds());

        await inbox.Received(4).StoreIncomingAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task a_cascaded_message_is_discarded_rather_than_failing_its_handler()
    {
        // The blast radius of this change is exactly "outside a handler, outside a transaction". Inside a
        // handler the publish is buffered against the context's own transaction and does not throw; the
        // send happens in FlushOutgoingMessagesAsync, whose loop catches, logs and records a
        // DiscardedEnvelope. So a store outage must NOT turn a handler that succeeded into a failure.
        //
        // With a real outbox enlisted (EF Core, Marten) the flush takes QuickSendAsync instead and never
        // reaches this code at all.
        var (host, inbox) = unreachableStore();
        using var _host = host;

        await Should.NotThrowAsync(() => host.MessageBus().InvokeAsync(new CascadingSource("one")));

        // Not vacuous: the cascade really did reach the store and really did fail there. Without this the
        // fact would pass just as happily if the message had never been routed at all.
        await inbox.Received().StoreIncomingAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task a_store_that_recovers_within_the_budget_still_succeeds()
    {
        // The other half of the contract: the retry is not cosmetic. Fail twice, then succeed, and the
        // caller never sees an exception.
        var store = new StoreWithAnUnreachableInbox();

        var attempts = 0;
        store.Inbox.StoreIncomingAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            attempts++;
            return attempts <= 2
                ? Task.FromException(new TimeoutException("still down"))
                : Task.CompletedTask;
        });

        using var host = hostWith(store);

        await host.MessageBus().PublishAsync(new StrandedMessage("four"));

        attempts.ShouldBe(3);
    }
}

public record StrandedMessage(string Name);

public static class StrandedMessageHandler
{
    public static void Handle(StrandedMessage message)
    {
    }
}

public record CascadingSource(string Name);

public static class CascadingSourceHandler
{
    public static StrandedMessage Handle(CascadingSource message)
    {
        return new StrandedMessage(message.Name);
    }
}
