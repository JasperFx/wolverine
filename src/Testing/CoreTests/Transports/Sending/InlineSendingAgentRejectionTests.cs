using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

/// <summary>
/// GH-4955. A destination that REFUSES a message -- a JetStream stream that is full under DiscardPolicy.New, a
/// failed Nats-Expected-* check, a message over the stream's size limit -- throws a SendRejectedException. An
/// inline sending agent used to hand that to its retry block, which retried three times at 50/100/250 ms, logged
/// "Error while trying to retry", discarded the message, and let SendAsync() return as if it had worked. The
/// server's answer does not change between attempts, so the rejection is sent once and thrown to the caller.
/// </summary>
public class InlineSendingAgentRejectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_rejection_is_not_retried_and_fails_the_send(bool useSyncRetryBlock)
    {
        var sender = new RefusingSender();
        var logger = new RecordingLogger();
        await using var agent = buildAgent(sender, logger, useSyncRetryBlock);

        var envelope = new Envelope { Message = new RejectedMessage(), Destination = sender.Destination };

        var rejection = await Should.ThrowAsync<SendRejectedException>(() => agent.EnqueueOutgoingAsync(envelope).AsTask());

        rejection.ShouldBeSameAs(sender.Rejection);
        sender.Attempts.ShouldBe(1);
        logger.Entries.ShouldNotContain(x => x.Level == LogLevel.Error);
        logger.Entries.ShouldContain(x => x.Level == LogLevel.Warning && x.Message.Contains("refused"));
    }

    [Fact]
    public async Task a_rejection_of_one_message_does_not_leak_into_the_next()
    {
        var sender = new RefusingSender { RefuseOnlyFirst = true };
        await using var agent = buildAgent(sender, new RecordingLogger(), useSyncRetryBlock: true);

        await Should.ThrowAsync<SendRejectedException>(() =>
            agent.EnqueueOutgoingAsync(new Envelope { Message = new RejectedMessage(), Destination = sender.Destination }).AsTask());

        // the next message is sent as usual
        await agent.EnqueueOutgoingAsync(new Envelope { Message = new RejectedMessage(), Destination = sender.Destination });
        sender.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task any_other_failure_is_still_retried()
    {
        // Fails once with an ordinary exception, then succeeds, so the retry block's second attempt sends it
        var sender = new RefusingSender { Transient = true };
        await using var agent = buildAgent(sender, new RecordingLogger(), useSyncRetryBlock: true);

        await agent.EnqueueOutgoingAsync(new Envelope { Message = new RejectedMessage(), Destination = sender.Destination });

        sender.Attempts.ShouldBe(2);
        sender.Sent.ShouldBe(1);
    }

    private static InlineSendingAgent buildAgent(ISender sender, ILogger logger, bool useSyncRetryBlock)
    {
        var tracker = Substitute.For<IMessageTracker>();
        return new InlineSendingAgent(logger, sender, new StubEndpoint(), tracker,
            new DurabilitySettings { UseSyncRetryBlock = useSyncRetryBlock });
    }

    public record RejectedMessage;

    private class RefusingSender : ISender
    {
        public SendRejectedException Rejection { get; } = new("the stream is full");
        public int Attempts { get; private set; }
        public int Sent { get; private set; }
        public bool RefuseOnlyFirst { get; init; }
        public bool Transient { get; init; }

        public bool SupportsNativeScheduledSend => false;
        public Uri Destination { get; } = new("stub://refusing");
        public Task<bool> PingAsync() => Task.FromResult(true);

        public ValueTask SendAsync(Envelope envelope)
        {
            Attempts++;

            if (Transient)
            {
                if (Attempts == 1)
                {
                    throw new InvalidOperationException("a blip");
                }

                Sent++;
                return ValueTask.CompletedTask;
            }

            if (RefuseOnlyFirst && Attempts > 1)
            {
                Sent++;
                return ValueTask.CompletedTask;
            }

            throw Rejection;
        }
    }

    private class StubEndpoint : Endpoint
    {
        public StubEndpoint() : base(new Uri("stub://refusing"), EndpointRole.Application)
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

    private class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
