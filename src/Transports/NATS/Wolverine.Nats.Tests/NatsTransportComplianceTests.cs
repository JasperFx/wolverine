// NATS Transport Compliance Tests
// These tests verify that the NATS transport conforms to Wolverine's transport contract

#if true

using JasperFx.Core;
using Wolverine.ComplianceTests.Compliance;
using Xunit;

namespace Wolverine.Nats.Tests;

public class InlineNatsTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Counter = 0;

    public InlineNatsTransportFixture() : base(new Uri("nats://subject/compliance.receiver"), 60)
    {
    }

    public async Task InitializeAsync()
    {
        var number = ++Counter;
        var receiverSubject = $"compliance.receiver.inline.{number}";
        var senderSubject = $"compliance.sender.inline.{number}";

        OutboundAddress = new Uri($"nats://subject/{receiverSubject}");

        // Check for NATS availability
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

        await SenderIs(opts =>
        {
            opts.UseNats(natsUrl).AutoProvision();
            opts.ListenToNatsSubject(senderSubject).ProcessInline();
            opts.PublishAllMessages().ToNatsSubject(receiverSubject).SendInline();
        });

        await ReceiverIs(opts =>
        {
            opts.UseNats(natsUrl).AutoProvision();
            opts.ListenToNatsSubject(receiverSubject).Named("receiver").ProcessInline();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("NATS Compliance")]
public class InlineNatsTransportComplianceTests : TransportCompliance<InlineNatsTransportFixture>;

public class BufferedNatsTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Counter = 0;

    public BufferedNatsTransportFixture() : base(new Uri("nats://subject/compliance.receiver"), 60)
    {
    }

    public async Task InitializeAsync()
    {
        var number = ++Counter;
        var receiverSubject = $"compliance.receiver.buffered.{number}";
        var senderSubject = $"compliance.sender.buffered.{number}";

        OutboundAddress = new Uri($"nats://subject/{receiverSubject}");

        // Check for NATS availability
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

        await SenderIs(opts =>
        {
            opts.UseNats(natsUrl).AutoProvision();
            opts.ListenToNatsSubject(senderSubject).BufferedInMemory();
            opts.PublishAllMessages().ToNatsSubject(receiverSubject).BufferedInMemory();
        });

        await ReceiverIs(opts =>
        {
            opts.UseNats(natsUrl).AutoProvision();
            opts.ListenToNatsSubject(receiverSubject).Named("receiver").BufferedInMemory();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

[Collection("NATS Compliance")]
public class BufferedNatsTransportComplianceTests : TransportCompliance<BufferedNatsTransportFixture>;

public class JetStreamNatsTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    public static int Counter = 0;

    public JetStreamNatsTransportFixture() : base(new Uri("nats://subject/compliance.receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var number = ++Counter;
        var streamName = $"COMPLIANCE_{number}";
        var receiverSubject = $"compliance.receiver.js.{number}";
        var senderSubject = $"compliance.sender.js.{number}";

        OutboundAddress = new Uri($"nats://subject/{receiverSubject}");

        // Check for NATS availability
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";

        await SenderIs(opts =>
        {
            opts.UseNats(natsUrl)
                .AutoProvision()
                .UseJetStream(js => js.MaxDeliver = 5)
                .DefineWorkQueueStream(streamName, $"compliance.*.js.{number}");

            opts.ListenToNatsSubject(senderSubject)
                .UseJetStream(streamName, $"sender-consumer-{number}");

            opts.PublishAllMessages().ToNatsSubject(receiverSubject);
        });

        await ReceiverIs(opts =>
        {
            opts.UseNats(natsUrl)
                .AutoProvision()
                .UseJetStream(js => js.MaxDeliver = 5)
                .DefineWorkQueueStream(streamName, $"compliance.*.js.{number}");

            opts.ListenToNatsSubject(receiverSubject)
                .Named("receiver")
                .UseJetStream(streamName, $"receiver-consumer-{number}");
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public override void BeforeEach()
    {
        // JetStream operations need a small cooldown between tests
        Thread.Sleep(1.Seconds());
    }
}

[Collection("NATS Compliance")]
public class JetStreamNatsTransportComplianceTests : TransportCompliance<JetStreamNatsTransportFixture>;

#endif
