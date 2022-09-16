using System;
using System.Threading.Tasks;
using Baseline.Dates;
using CoreTests.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using TestingSupport;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Acceptance;

public class discarding_expired_envelopes
{
    [Fact]
    public async Task can_discard_an_envelope_if_expired()
    {
        var logger = Substitute.For<IMessageLogger>();

        using var runtime = WolverineHost.For(x =>
        {
            x.Handlers.DisableConventionalDiscovery();
            x.Services.AddSingleton(logger);
        });

        var pipeline = runtime.Get<IHandlerPipeline>();

        var envelope = ObjectMother.Envelope();
        envelope.DeliverBy = DateTimeOffset.Now.Subtract(1.Minutes());
        var channel = Substitute.For<IChannelCallback>();

        await pipeline.InvokeAsync(envelope, channel);

#pragma warning disable 4014
        channel.Received().CompleteAsync(envelope);
#pragma warning restore 4014
    }
}
