using Shouldly;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-4517: "NATS connection not initialized" / "JetStream context not initialized" named none of
/// the three causes a user can act on -- and for JetStream in particular, EnableJetStream = false
/// is a cause the message never mentioned.
/// </summary>
public class transport_message_quality_4517
{
    [Fact]
    public void connection_before_startup_names_both_causes()
    {
        var transport = new NatsTransport();

        var ex = Should.Throw<InvalidOperationException>(() => transport.Connection);

        ex.Message.ShouldContain("UseNats()");
        ex.Message.ShouldContain("has not been started");
    }

    [Fact]
    public void jetstream_context_before_startup_also_names_the_disabled_case()
    {
        var transport = new NatsTransport();

        var ex = Should.Throw<InvalidOperationException>(() => transport.JetStreamContext);

        ex.Message.ShouldContain("UseNats()");
        ex.Message.ShouldContain("has not been started");
        ex.Message.ShouldContain("EnableJetStream");
    }
}
