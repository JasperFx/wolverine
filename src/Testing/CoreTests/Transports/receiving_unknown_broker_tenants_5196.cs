using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports;

/// <summary>
/// GH-5196, issue test 10: the receiving side of <see cref="TenantedIdBehavior.DeadLetterUnknownTenants" />. A message
/// received at a tenant-aware broker endpoint for a tenant id no registered tenant matches is dead lettered with the
/// same <see cref="UnknownBrokerTenantException" /> reason shape as the sending side
/// </summary>
public class unknown_broker_tenant_guard_5196
{
    private static readonly Uri Endpoint = new("rabbitmq://queue/incoming");

    private static UnknownBrokerTenantGuard guardFor(TenantedIdBehavior behavior, params string[] tenants)
    {
        var endpoint = new LocalQueue("guarded");
        UnknownBrokerTenantGuard.Install(endpoint, behavior, tenants);
        return endpoint.UnknownTenantGuard!;
    }

    private static Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        return envelope;
    }

    [Fact]
    public void only_the_7_0_default_installs_a_guard()
    {
        var endpoint = new LocalQueue("guarded");

        UnknownBrokerTenantGuard.Install(endpoint, TenantedIdBehavior.DeadLetterUnknownTenants, ["one"]);
        endpoint.UnknownTenantGuard.ShouldNotBeNull();

        foreach (var behavior in new[]
                 {
                     TenantedIdBehavior.FallbackToDefault, TenantedIdBehavior.TenantIdRequired,
                     TenantedIdBehavior.IgnoreUnknownTenants
                 })
        {
            UnknownBrokerTenantGuard.Install(endpoint, behavior, ["one"]);
            endpoint.UnknownTenantGuard.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(StorageConstants.DefaultTenantId)]
    [InlineData(TransportConstants.Default)]
    public void no_tenant_id_is_handled(string? tenantId)
    {
        guardFor(TenantedIdBehavior.DeadLetterUnknownTenants, "one").Check(envelopeFor(tenantId)).ShouldBeNull();
    }

    [Fact]
    public void a_registered_tenant_is_handled()
    {
        guardFor(TenantedIdBehavior.DeadLetterUnknownTenants, "one", "two").Check(envelopeFor("two")).ShouldBeNull();
    }

    [Theory]
    [InlineData("three")]
    // Issue test 9: the same matching rules as the sending side, exact ordinal match with no trimming
    [InlineData("ONE")]
    [InlineData(" one")]
    [InlineData("one ")]
    public void an_unknown_tenant_gets_the_same_reason_shape_as_the_sending_side(string tenantId)
    {
        var envelope = envelopeFor(tenantId);
        envelope.MessageType = "Incoming";

        var ex = guardFor(TenantedIdBehavior.DeadLetterUnknownTenants, "one").Check(envelope);

        ex.ShouldNotBeNull();
        ex.ShouldBeAssignableTo<UnknownTenantIdException>();
        ex.TenantId.ShouldBe(tenantId);
        ex.MessageType.ShouldBe("Incoming");
        ex.Endpoint.ShouldBe(new Uri("local://guarded/"));
        ex.Message.ShouldContain($"Unknown tenant id '{tenantId}' for message type 'Incoming' at endpoint local://guarded/");
        ex.Message.ShouldContain("moved to the dead letter queue");

        // The sending side's reason opens the same way
        new UnknownBrokerTenantException(tenantId, "Incoming", new Uri("local://guarded/")).Message
            .ShouldStartWith($"Unknown tenant id '{tenantId}' for message type 'Incoming' at endpoint local://guarded/");
    }
}

public class receiving_unknown_broker_tenants_through_the_pipeline_5196 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<TenantedIncomingHandler>();
                opts.PublishMessage<TenantedIncoming>().ToLocalQueue("tenanted");
            }).StartAsync();

        // Stand in for a tenant-aware broker endpoint: the guard is what the transports install when they build the
        // listener of a tenant-aware endpoint, and the endpoint's pipeline reads it for every message
        var endpoint = _host.GetRuntime().Endpoints.EndpointFor(new Uri("local://tenanted"))!;
        UnknownBrokerTenantGuard.Install(endpoint, TenantedIdBehavior.DeadLetterUnknownTenants, ["one", "two"]);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task an_unknown_tenant_is_dead_lettered_and_the_tracked_session_records_it()
    {
        var session = await _host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new TenantedIncoming("bogus"), new DeliveryOptions { TenantId = "bogus" });

        // Issue test 11, receiving side: TrackActivity records the dead lettering and its reason
        var record = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue)
            .Single(x => x.Message is TenantedIncoming);
        var ex = record.Exception.ShouldBeOfType<UnknownBrokerTenantException>();
        ex.TenantId.ShouldBe("bogus");
        ex.Endpoint.ShouldBe(new Uri("local://tenanted/"));

        session.Executed.MessagesOf<TenantedIncoming>().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_registered_tenant_is_handled()
    {
        var session = await _host.TrackActivity()
            .SendMessageAndWaitAsync(new TenantedIncoming("one"), new DeliveryOptions { TenantId = "one" });

        session.Executed.SingleEnvelope<TenantedIncoming>().TenantId.ShouldBe("one");
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }

    [Fact]
    public async Task no_tenant_id_is_handled()
    {
        var session = await _host.TrackActivity().SendMessageAndWaitAsync(new TenantedIncoming("none"));

        session.Executed.MessagesOf<TenantedIncoming>().ShouldHaveSingleItem();
        session.MovedToErrorQueue.Envelopes().ShouldBeEmpty();
    }
}

public record TenantedIncoming(string Name);

public class TenantedIncomingHandler
{
    public static void Handle(TenantedIncoming message)
    {
    }
}
