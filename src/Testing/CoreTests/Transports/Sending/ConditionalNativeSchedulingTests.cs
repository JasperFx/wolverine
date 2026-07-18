using System;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

public class ConditionalNativeSchedulingTests
{
    private readonly DateTimeOffset theCurrentTime = DateTimeOffset.UtcNow;

    private Envelope envelopeScheduledIn(TimeSpan delay)
    {
        return new Envelope { ScheduledTime = theCurrentTime.Add(delay) };
    }

    [Fact]
    public void agent_without_native_scheduling_is_false_regardless_of_envelope()
    {
        var agent = Substitute.For<ISendingAgent>();
        agent.SupportsNativeScheduledSend.Returns(false);

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(5.Seconds()), theCurrentTime)
            .ShouldBeFalse();
    }

    [Fact]
    public void agent_with_unconditional_native_scheduling_is_true()
    {
        var agent = Substitute.For<ISendingAgent>();
        agent.SupportsNativeScheduledSend.Returns(true);

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(30.Days()), theCurrentTime)
            .ShouldBeTrue();
    }

    [Fact]
    public void inline_agent_consults_the_conditional_sender_per_envelope()
    {
        var sender = new ConditionalSender { Limit = 15.Minutes() };
        var agent = new InlineSendingAgent(NullLogger.Instance, sender, new StubEndpoint(),
            Substitute.For<IMessageTracker>(), new DurabilitySettings());

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(5.Minutes()), theCurrentTime)
            .ShouldBeTrue();

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(20.Minutes()), theCurrentTime)
            .ShouldBeFalse();
    }

    [Fact]
    public void conditional_interface_is_not_consulted_when_the_sender_opts_out_entirely()
    {
        var sender = new ConditionalSender { Limit = 15.Minutes(), SupportsNativeScheduledSend = false };
        var agent = new InlineSendingAgent(NullLogger.Instance, sender, new StubEndpoint(),
            Substitute.For<IMessageTracker>(), new DurabilitySettings());

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(5.Minutes()), theCurrentTime)
            .ShouldBeFalse();
    }

    [Fact]
    public void tenanted_sender_delegates_the_conditional_decision_to_the_default_sender()
    {
        var inner = new ConditionalSender { Limit = 15.Minutes() };
        var tenanted = new TenantedSender(new Uri("stub://one"), TenantedIdBehavior.FallbackToDefault, inner);
        var agent = new InlineSendingAgent(NullLogger.Instance, tenanted, new StubEndpoint(),
            Substitute.For<IMessageTracker>(), new DurabilitySettings());

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(5.Minutes()), theCurrentTime)
            .ShouldBeTrue();

        agent.SupportsNativeScheduledSendFor(envelopeScheduledIn(20.Minutes()), theCurrentTime)
            .ShouldBeFalse();
    }

    private class ConditionalSender : ISender, IConditionalNativeScheduling
    {
        public TimeSpan Limit { get; set; } = TimeSpan.MaxValue;

        public bool SupportsNativeScheduledSend { get; set; } = true;

        public Uri Destination { get; } = new("stub://one");

        public Task<bool> PingAsync()
        {
            return Task.FromResult(true);
        }

        public ValueTask SendAsync(Envelope envelope)
        {
            return ValueTask.CompletedTask;
        }

        public bool CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow)
        {
            return envelope.ScheduledTime is not { } time || time.Subtract(utcNow) <= Limit;
        }
    }

    private class StubEndpoint : Endpoint
    {
        public StubEndpoint() : base(new Uri("stub://one"), EndpointRole.Application)
        {
        }

        public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        {
            throw new NotSupportedException();
        }

        protected override ISender CreateSender(IWolverineRuntime runtime)
        {
            throw new NotSupportedException();
        }

        protected override bool supportsMode(EndpointMode mode)
        {
            return true;
        }
    }
}
