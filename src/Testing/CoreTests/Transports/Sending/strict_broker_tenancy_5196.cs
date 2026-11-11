using CoreTests.Runtime;
using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

/// <summary>
/// GH-5196. A broker is a trust boundary: under the 7.0 default, <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" />,
/// a message for a tenant id no registered tenant matches is never sent, to the default broker or any other tenant's
/// </summary>
public class tenanted_sender_dead_letters_unknown_tenants_5196
{
    private readonly ISender theDefault = Substitute.For<ISender>();
    private readonly ISender one = Substitute.For<ISender>();
    private readonly ISender two = Substitute.For<ISender>();
    private readonly TenantedSender theSender;

    public tenanted_sender_dead_letters_unknown_tenants_5196()
    {
        theSender = new TenantedSender("tcp://localhost:5196".ToUri(), TenantedIdBehavior.DeadLetterUnknownTenants,
            theDefault);
        theSender.RegisterSender("one", one);
        theSender.RegisterSender("two", two);
    }

    private static Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        return envelope;
    }

    private async Task nothingWasSentAnywhere()
    {
        await theDefault.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await one.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await two.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [Fact]
    public void a_default_sender_is_required()
    {
        Should.Throw<ArgumentNullException>(() =>
            new TenantedSender("tcp://localhost:5196".ToUri(), TenantedIdBehavior.DeadLetterUnknownTenants, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(StorageConstants.DefaultTenantId)]
    [InlineData(TransportConstants.Default)]
    public async Task no_tenant_id_goes_to_the_default_broker(string? tenantId)
    {
        var envelope = envelopeFor(tenantId);

        theSender.IsUnknownTenantToDeadLetter(envelope).ShouldBeFalse();
        await theSender.SendAsync(envelope);

        await theDefault.Received(1).SendAsync(envelope);
        await one.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await two.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [Fact]
    public async Task a_known_tenant_goes_to_that_tenants_broker_only()
    {
        var envelope = envelopeFor("two");

        theSender.IsUnknownTenantToDeadLetter(envelope).ShouldBeFalse();
        await theSender.SendAsync(envelope);

        await two.Received(1).SendAsync(envelope);
        await theDefault.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await one.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    // Tenant ids match exactly: ordinal, case sensitive and untrimmed. A tenant-aware listener stamps the id its
    // tenant was registered with, so the receiving side uses the same rule
    [Theory]
    [InlineData("three")]
    [InlineData("One")]
    [InlineData("ONE")]
    [InlineData(" one")]
    [InlineData("one ")]
    public async Task an_unknown_tenant_is_never_sent_anywhere(string tenantId)
    {
        var envelope = envelopeFor(tenantId);

        theSender.IsUnknownTenantToDeadLetter(envelope).ShouldBeTrue();

        var exception = await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await theSender.SendAsync(envelope));

        exception.TenantId.ShouldBe(tenantId);
        exception.MessageType.ShouldBe(envelope.MessageType);
        exception.Endpoint.ShouldBe(theSender.Destination);
        exception.Message.ShouldContain($"'{tenantId}'");
        exception.Message.ShouldContain(envelope.MessageType!);
        exception.Message.ShouldContain(theSender.Destination.ToString());

        // Existing catch (UnknownTenantIdException) blocks keep catching it
        exception.ShouldBeAssignableTo<UnknownTenantIdException>();

        await nothingWasSentAnywhere();
    }

    [Fact]
    public async Task an_unknown_tenant_is_not_cached_so_a_tenant_registered_later_is_found()
    {
        var envelope = envelopeFor("late");
        await Should.ThrowAsync<UnknownBrokerTenantException>(async () => await theSender.SendAsync(envelope));

        var late = Substitute.For<ISender>();
        theSender.RegisterSender("late", late);

        theSender.IsUnknownTenantToDeadLetter(envelope).ShouldBeFalse();
        await theSender.SendAsync(envelope);
        await late.Received(1).SendAsync(envelope);
    }

    [Fact]
    public void other_behaviors_never_ask_for_a_dead_letter()
    {
        var fallback = new TenantedSender("tcp://localhost:5196".ToUri(), TenantedIdBehavior.FallbackToDefault,
            theDefault);
        fallback.IsUnknownTenantToDeadLetter(envelopeFor("three")).ShouldBeFalse();

        var required = new TenantedSender("tcp://localhost:5196".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        required.IsUnknownTenantToDeadLetter(envelopeFor("three")).ShouldBeFalse();
    }

    // The explicit 6.x choices behave exactly as they did
    [Fact]
    public async Task fallback_to_default_still_sends_an_unknown_tenant_to_the_default_broker()
    {
        var fallback = new TenantedSender("tcp://localhost:5196".ToUri(), TenantedIdBehavior.FallbackToDefault,
            theDefault);
        fallback.RegisterSender("one", one);

        var envelope = envelopeFor("three");
        await fallback.SendAsync(envelope);

        await theDefault.Received(1).SendAsync(envelope);
    }

    [Fact]
    public async Task tenant_id_required_still_throws_for_an_unknown_tenant()
    {
        var required = new TenantedSender("tcp://localhost:5196".ToUri(), TenantedIdBehavior.TenantIdRequired, null);
        required.RegisterSender("one", one);

        await Should.ThrowAsync<InvalidOperationException>(async () => await required.SendAsync(envelopeFor("three")));
        await Should.ThrowAsync<ArgumentNullException>(async () => await required.SendAsync(envelopeFor(null)));
    }
}

/// <summary>
/// GH-5196. The sending agents dead letter a message for an unknown tenant before it is stored or sent, write the
/// dead letter to the main message store, record it for tracked sessions, and throw when there is no message store
/// </summary>
public class sending_agents_dead_letter_unknown_tenants_5196 : IAsyncDisposable
{
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly ISender theDefault = Substitute.For<ISender>();
    private readonly ISender one = Substitute.For<ISender>();
    private readonly TenantedSender theSender;
    private readonly LocalQueue theEndpoint = new("strict-tenancy");
    private readonly IMessageOutbox theOutbox = Substitute.For<IMessageOutbox>();
    private readonly List<IAsyncDisposable> _disposables = new();

    public sending_agents_dead_letter_unknown_tenants_5196()
    {
        theDefault.Destination.Returns(theEndpoint.Uri);
        theSender = new TenantedSender(theEndpoint.Uri, TenantedIdBehavior.DeadLetterUnknownTenants, theDefault);
        theSender.RegisterSender("one", one);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
    }

    private Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        envelope.Destination = theEndpoint.Uri;
        return envelope;
    }

    private DurableSendingAgent durableAgent(IWolverineRuntime runtime)
    {
        var agent = new DurableSendingAgent(theSender, runtime.Options.Durability, NullLogger.Instance,
            Substitute.For<IMessageTracker>(), theOutbox, theEndpoint, runtime, null);
        _disposables.Add(agent);
        return agent;
    }

    private BufferedSendingAgent bufferedAgent(IWolverineRuntime runtime)
    {
        var agent = new BufferedSendingAgent(NullLogger.Instance, Substitute.For<IMessageTracker>(), theSender,
            runtime.Options.Durability, theEndpoint, runtime, null);
        _disposables.Add(agent);
        return agent;
    }

    private InlineSendingAgent inlineAgent(IWolverineRuntime runtime)
    {
        return new InlineSendingAgent(NullLogger.Instance, theSender, theEndpoint, Substitute.For<IMessageTracker>(),
            runtime.Options.Durability, runtime, null);
    }

    private async Task theDeadLetterWasWrittenFor(Envelope envelope)
    {
        await theRuntime.Storage.Inbox.Received(1).MoveToDeadLetterStorageAsync(envelope,
            Arg.Is<UnknownBrokerTenantException>(e => e.TenantId == envelope.TenantId && e.Endpoint == theEndpoint.Uri));

        // Tracked sessions see it as moved to the error queue
        theRuntime.MessageTracking.Received(1)
            .MovedToErrorQueue(envelope, Arg.Is<Exception>(e => e is UnknownBrokerTenantException));

        // Still carries its tenant id, so the dead letter is tagged with it
        envelope.TenantId.ShouldBe("unknown");
    }

    private async Task nothingWasSent()
    {
        // Give a background sending block a moment, should anything have been posted to it
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await theDefault.DidNotReceiveWithAnyArgs().SendAsync(default!);
        await one.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }

    [Fact]
    public async Task durable_store_and_forward_never_puts_an_unknown_tenant_in_the_outbox()
    {
        var agent = durableAgent(theRuntime);
        var envelope = envelopeFor("unknown");

        await agent.StoreAndForwardAsync(envelope);

        await theDeadLetterWasWrittenFor(envelope);
        await theOutbox.DidNotReceiveWithAnyArgs().StoreOutgoingAsync(default(Envelope)!, default);
        await theOutbox.DidNotReceiveWithAnyArgs().DeleteOutgoingAsync(default(Envelope)!);
        await nothingWasSent();
    }

    [Fact]
    public async Task durable_enqueue_after_an_outbox_transaction_removes_the_outbox_row()
    {
        // A handler's transaction already wrote the outbox row, then flushes the envelope to the agent
        var agent = durableAgent(theRuntime);
        var envelope = envelopeFor("unknown");

        await agent.EnqueueOutgoingAsync(envelope);

        await theDeadLetterWasWrittenFor(envelope);
        await theOutbox.Received(1).DeleteOutgoingAsync(envelope);
        await nothingWasSent();
    }

    [Fact]
    public async Task durable_agent_still_sends_a_known_tenant()
    {
        var agent = durableAgent(theRuntime);
        var envelope = envelopeFor("one");

        await agent.EnqueueOutgoingAsync(envelope);

        await theRuntime.Storage.Inbox.DidNotReceiveWithAnyArgs().MoveToDeadLetterStorageAsync(default!, default);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await one.Received(1).SendAsync(envelope);
    }

    [Fact]
    public async Task buffered_agent_dead_letters_an_unknown_tenant()
    {
        var agent = bufferedAgent(theRuntime);
        var envelope = envelopeFor("unknown");

        await agent.StoreAndForwardAsync(envelope);

        await theDeadLetterWasWrittenFor(envelope);
        await nothingWasSent();
    }

    [Fact]
    public async Task inline_agent_dead_letters_an_unknown_tenant()
    {
        var agent = inlineAgent(theRuntime);
        var envelope = envelopeFor("unknown");

        await agent.EnqueueOutgoingAsync(envelope);

        await theDeadLetterWasWrittenFor(envelope);
        await nothingWasSent();
    }

    [Fact]
    public async Task the_dead_letter_goes_to_the_main_store_of_a_database_per_tenant_store()
    {
        var main = Substitute.For<IMessageStore>();
        var tenants = Substitute.For<ITenantedMessageSource>();
        var multiTenanted = new MultiTenantedMessageStore(main, theRuntime, tenants);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(theRuntime.Options);
        runtime.Storage.Returns(multiTenanted);
        runtime.MessageTracking.Returns(theRuntime.MessageTracking);

        var agent = inlineAgent(runtime);
        var envelope = envelopeFor("unknown");

        await agent.EnqueueOutgoingAsync(envelope);

        await main.Inbox.Received(1).MoveToDeadLetterStorageAsync(envelope, Arg.Any<UnknownBrokerTenantException>());
        await nothingWasSent();
    }

    [Fact]
    public async Task with_no_message_store_the_send_throws_and_nothing_is_sent()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(theRuntime.Options);
        runtime.Storage.Returns(new NullMessageStore());

        var envelope = envelopeFor("unknown");

        await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await bufferedAgent(runtime).StoreAndForwardAsync(envelope));
        await Should.ThrowAsync<UnknownBrokerTenantException>(async () =>
            await inlineAgent(runtime).EnqueueOutgoingAsync(envelopeFor("unknown")));

        await nothingWasSent();
    }

    [Fact]
    public async Task an_inline_send_through_the_outbox_dead_letters_an_unknown_tenant()
    {
        // ServerlessWithOutbox and the durability pass send through SendToBrokerAsync, not the agent's own loop
        var unknown = envelopeFor("unknown");
        var known = envelopeFor("one");
        var result = await theSender.SendToBrokerAsync([unknown, known]);

        result.WasAccepted(known).ShouldBeTrue();
        result.WasAccepted(unknown).ShouldBeFalse();
        result.IsSerializationFailure(unknown, out var exception).ShouldBeTrue();
        exception.ShouldBeOfType<UnknownBrokerTenantException>();
        await theDefault.DidNotReceiveWithAnyArgs().SendAsync(default!);
    }
}

/// <summary>
/// GH-5196. The broker transports take <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" /> by default in 7.0,
/// and <see cref="WolverineOptions.RestoreV6Defaults" /> puts back the 6.x default, <see cref="TenantedIdBehavior.FallbackToDefault" />.
/// Named by the migration guide's 7.0 defaults table
/// </summary>
public class restore_v6_broker_tenancy_defaults
{
    private static async Task<SlowFailingTransport> startedTransport(Action<WolverineOptions>? configure = null,
        Action<SlowFailingTransport>? configureTransport = null)
    {
        var runtime = new MockWolverineRuntime();
        configure?.Invoke(runtime.Options);

        var transport = new SlowFailingTransport { SucceedOnAttempt = 1 };
        configureTransport?.Invoke(transport);

        await transport.InitializeAsync(runtime);
        return transport;
    }

    [Fact]
    public async Task the_7_0_default_dead_letters_unknown_tenants()
    {
        new SlowFailingTransport().TenantedIdBehavior.ShouldBe(TenantedIdBehavior.DeadLetterUnknownTenants);

        var transport = await startedTransport();
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.DeadLetterUnknownTenants);
    }

    [Fact]
    public async Task restore_v6_defaults_falls_back_to_the_default_broker()
    {
        var transport = await startedTransport(o => o.RestoreV6Defaults());
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.FallbackToDefault);
    }

    [Theory]
    [InlineData(TenantedIdBehavior.DeadLetterUnknownTenants)]
    [InlineData(TenantedIdBehavior.TenantIdRequired)]
    [InlineData(TenantedIdBehavior.IgnoreUnknownTenants)]
    public async Task an_explicit_setting_wins_over_restore_v6_defaults(TenantedIdBehavior behavior)
    {
        var transport = await startedTransport(o => o.RestoreV6Defaults(), t => t.TenantedIdBehavior = behavior);
        transport.TenantedIdBehavior.ShouldBe(behavior);
    }

    [Fact]
    public async Task an_explicit_fallback_wins_over_the_7_0_default()
    {
        var transport = await startedTransport(configureTransport: t => t.TenantedIdBehavior = TenantedIdBehavior.FallbackToDefault);
        transport.TenantedIdBehavior.ShouldBe(TenantedIdBehavior.FallbackToDefault);
    }
}
