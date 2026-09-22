using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Handlers;

/// <summary>
/// GH-4532: returning an <see cref="Envelope"/> from a handler is refused, and people try it because
/// they want control over how a cascading message is delivered. The refusal has to name the supported
/// ways of doing that, or the reader is left with a "no" and nowhere to go.
/// </summary>
public class handler_that_creates_an_envelope
{
    [Fact]
    public async Task the_refusal_names_the_supported_alternatives()
    {
        var ex = await Should.ThrowAsync<InvalidHandlerException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EnvelopeReturningHandler));
                }).StartAsync(TestContext.Current.CancellationToken);

            await host.InvokeMessageAndWaitAsync(new MessageThatWantsAnEnvelope());
        });

        ex.Message.ShouldContain(typeof(Envelope).FullNameInCode());

        // all four supported ways to control delivery of a cascading message
        ex.Message.ShouldContain("DeliveryMessage<T>");
        ex.Message.ShouldContain(nameof(DeliveryOptions));
        ex.Message.ShouldContain(nameof(ISendMyself));
        ex.Message.ShouldContain(nameof(OutgoingMessages));
        ex.Message.ShouldContain(nameof(IMessageBus));
    }
}

public record MessageThatWantsAnEnvelope;

public static class EnvelopeReturningHandler
{
    public static Envelope Handle(MessageThatWantsAnEnvelope message)
    {
        return new Envelope(message);
    }
}
