using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Nats.Tests.Helpers;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

#region Unit Tests - No NATS Infrastructure Required

/// <summary>
/// Unit tests for the DefaultTenantSubjectMapper - no NATS connection required
/// </summary>
public class DefaultTenantSubjectMapperTests
{
    [Fact]
    public void maps_subject_with_tenant_prefix()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.MapSubject("orders", "tenant1").ShouldBe("tenant1.orders");
        mapper.MapSubject("orders", "tenant2").ShouldBe("tenant2.orders");
    }
    
    [Fact]
    public void maps_nested_subject_with_tenant_prefix()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.MapSubject("orders.created", "tenant1").ShouldBe("tenant1.orders.created");
        mapper.MapSubject("orders.shipped.international", "tenant2").ShouldBe("tenant2.orders.shipped.international");
    }

    [Fact]
    public void returns_original_subject_when_tenant_is_empty()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.MapSubject("orders", "").ShouldBe("orders");
        mapper.MapSubject("orders", null!).ShouldBe("orders");
    }
    
    [Fact]
    public void extracts_tenant_id_from_subject()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.ExtractTenantId("tenant1.orders").ShouldBe("tenant1");
        mapper.ExtractTenantId("tenant2.orders").ShouldBe("tenant2");
    }
    
    [Fact]
    public void extracts_tenant_id_from_nested_subject()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.ExtractTenantId("tenant1.orders.created").ShouldBe("tenant1");
    }
    
    [Fact]
    public void returns_null_when_no_tenant_in_subject()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.ExtractTenantId("orders").ShouldBeNull();
        mapper.ExtractTenantId("").ShouldBeNull();
        mapper.ExtractTenantId(null!).ShouldBeNull();
    }
    
    [Fact]
    public void generates_wildcard_subscription_pattern()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        mapper.GetSubscriptionPattern("orders").ShouldBe("*.orders");
        mapper.GetSubscriptionPattern("orders.created").ShouldBe("*.orders.created");
    }

    [Fact]
    public void normalizes_slashes_in_tenant_id()
    {
        var mapper = new DefaultTenantSubjectMapper();
        
        // Tenant IDs with slashes get normalized to dots
        mapper.MapSubject("orders", "org/team").ShouldBe("org.team.orders");
    }
    
    [Fact]
    public void custom_separator_works()
    {
        var mapper = new DefaultTenantSubjectMapper(separator: "-");
        
        mapper.MapSubject("orders", "tenant1").ShouldBe("tenant1-orders");
        mapper.ExtractTenantId("tenant1-orders").ShouldBe("tenant1");
        mapper.GetSubscriptionPattern("orders").ShouldBe("*-orders");
    }
}

/// <summary>
/// Unit tests for TenantedSender routing logic - uses mocked senders
/// Following the pattern from Wolverine's CoreTests/Transports/Sending/TenantedSenderTests.cs
/// </summary>
public class TenantedSenderRoutingTests
{
    private readonly ISender _defaultSender = Substitute.For<ISender>();
    private readonly ISender _tenant1Sender = Substitute.For<ISender>();
    private readonly ISender _tenant2Sender = Substitute.For<ISender>();
    private readonly ISender _tenant3Sender = Substitute.For<ISender>();

    public TenantedSenderRoutingTests()
    {
        _defaultSender.Destination.Returns(new Uri("nats://subject/orders"));
        _tenant1Sender.Destination.Returns(new Uri("nats://subject/tenant1.orders"));
        _tenant2Sender.Destination.Returns(new Uri("nats://subject/tenant2.orders"));
        _tenant3Sender.Destination.Returns(new Uri("nats://subject/tenant3.orders"));
    }

    [Fact]
    public async Task routes_messages_to_correct_tenant_sender()
    {
        var tenantedSender = new TenantedSender(
            new Uri("nats://subject/orders"),
            TenantedIdBehavior.TenantIdRequired,
            null);
        
        tenantedSender.RegisterSender("tenant1", _tenant1Sender);
        tenantedSender.RegisterSender("tenant2", _tenant2Sender);
        tenantedSender.RegisterSender("tenant3", _tenant3Sender);

        var e1 = new Envelope { TenantId = "tenant1" };
        var e2 = new Envelope { TenantId = "tenant2" };
        var e3 = new Envelope { TenantId = "tenant3" };

        await tenantedSender.SendAsync(e1);
        await tenantedSender.SendAsync(e2);
        await tenantedSender.SendAsync(e3);

        await _tenant1Sender.Received(1).SendAsync(e1);
        await _tenant2Sender.Received(1).SendAsync(e2);
        await _tenant3Sender.Received(1).SendAsync(e3);
    }

    [Fact]
    public void throws_when_default_sender_required_but_not_provided()
    {
        Should.Throw<ArgumentNullException>(() =>
        {
            _ = new TenantedSender(
                new Uri("nats://subject/orders"),
                TenantedIdBehavior.FallbackToDefault,
                null);
        });
    }

    [Fact]
    public async Task throws_when_tenant_id_required_but_missing()
    {
        var tenantedSender = new TenantedSender(
            new Uri("nats://subject/orders"),
            TenantedIdBehavior.TenantIdRequired,
            null);
        
        tenantedSender.RegisterSender("tenant1", _tenant1Sender);

        var envelope = new Envelope { TenantId = null };

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await tenantedSender.SendAsync(envelope);
        });
    }

    [Fact]
    public async Task throws_when_tenant_id_required_but_empty()
    {
        var tenantedSender = new TenantedSender(
            new Uri("nats://subject/orders"),
            TenantedIdBehavior.TenantIdRequired,
            null);
        
        tenantedSender.RegisterSender("tenant1", _tenant1Sender);

        var envelope = new Envelope { TenantId = string.Empty };

        await Should.ThrowAsync<ArgumentNullException>(async () =>
        {
            await tenantedSender.SendAsync(envelope);
        });
    }

    [Fact]
    public async Task falls_back_to_default_sender_when_tenant_missing()
    {
        var tenantedSender = new TenantedSender(
            new Uri("nats://subject/orders"),
            TenantedIdBehavior.FallbackToDefault,
            _defaultSender);
        
        tenantedSender.RegisterSender("tenant1", _tenant1Sender);
        tenantedSender.RegisterSender("tenant2", _tenant2Sender);

        var e1 = new Envelope { TenantId = null };
        var e2 = new Envelope { TenantId = "tenant2" };
        var e3 = new Envelope { TenantId = string.Empty };

        await tenantedSender.SendAsync(e1);
        await tenantedSender.SendAsync(e2);
        await tenantedSender.SendAsync(e3);

        await _defaultSender.Received(1).SendAsync(e1);
        await _tenant2Sender.Received(1).SendAsync(e2);
        await _defaultSender.Received(1).SendAsync(e3);
    }

    [Fact]
    public async Task falls_back_to_default_for_unknown_tenant()
    {
        var tenantedSender = new TenantedSender(
            new Uri("nats://subject/orders"),
            TenantedIdBehavior.FallbackToDefault,
            _defaultSender);
        
        tenantedSender.RegisterSender("tenant1", _tenant1Sender);

        var envelope = new Envelope { TenantId = "unknown_tenant" };

        await tenantedSender.SendAsync(envelope);

        await _defaultSender.Received(1).SendAsync(envelope);
    }

    [Fact]
    public async Task throws_for_unknown_tenant_when_required()
    {
        var tenantedSender = new TenantedSender(
            new Uri("nats://subject/orders"),
            TenantedIdBehavior.TenantIdRequired,
            null);
        
        tenantedSender.RegisterSender("tenant1", _tenant1Sender);

        var envelope = new Envelope { TenantId = "unknown_tenant" };

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await tenantedSender.SendAsync(envelope);
        });
    }
}

/// <summary>
/// Unit tests for NatsTenant configuration
/// </summary>
public class NatsTenantTests
{
    [Fact]
    public void creates_tenant_with_id()
    {
        var tenant = new NatsTenant("tenant1");
        
        tenant.TenantId.ShouldBe("tenant1");
        tenant.SubjectMapper.ShouldBeNull();
        tenant.ConnectionString.ShouldBeNull();
    }

    [Fact]
    public void throws_when_tenant_id_is_null()
    {
        Should.Throw<ArgumentNullException>(() => new NatsTenant(null!));
    }

    [Fact]
    public void can_set_custom_subject_mapper()
    {
        var tenant = new NatsTenant("tenant1");
        var mapper = new DefaultTenantSubjectMapper(separator: "-");
        
        tenant.SubjectMapper = mapper;
        
        tenant.SubjectMapper.ShouldBe(mapper);
    }
}

#endregion

#region Integration Tests - Require NATS Connection

/// <summary>
/// Integration tests for NATS multi-tenancy using the dual-host pattern
/// (sender and receiver as separate hosts) for reliable tracking.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class MultiTenancyIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _sender;
    private IHost? _receiver;
    private string _baseSubject = null!;
    private string? _natsUrl;

    public MultiTenancyIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        _baseSubject = $"test.multitenancy.{Guid.NewGuid():N}";
        
        _output.WriteLine($"Using NATS URL: {_natsUrl}");
        _output.WriteLine($"Base subject: {_baseSubject}");

        // Check if NATS is available
        if (!await IsNatsAvailable(_natsUrl))
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }
        
        // Create sender host with multi-tenancy enabled
        _sender = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "MultiTenancySender";
                
                opts.UseNats(_natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenant1")
                    .AddTenant("tenant2");
                
                opts.PublishMessage<TenantTestMessage>()
                    .ToNatsSubject(_baseSubject);
            })
            .StartAsync();

        // Create receiver host that listens for messages
        // The receiver uses wildcard subscription to receive all tenant messages
        _receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "MultiTenancyReceiver";
                
                opts.UseNats(_natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenant1")
                    .AddTenant("tenant2");
                
                opts.ListenToNatsSubject(_baseSubject);
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_sender != null)
        {
            await _sender.StopAsync();
            _sender.Dispose();
        }
        if (_receiver != null)
        {
            await _receiver.StopAsync();
            _receiver.Dispose();
        }
    }

    [Fact]
    public async Task messages_are_routed_to_tenant_specific_subjects()
    {
        if (_sender == null || _receiver == null)
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }

        var msg1 = new TenantTestMessage(Guid.NewGuid(), "Message for tenant 1");
        
        _output.WriteLine($"Sending message {msg1.Id} with tenant1");

        // Send message with tenant1 and track across both hosts
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(msg1, new DeliveryOptions { TenantId = "tenant1" });

        // Verify message was sent
        var sentMessage = session.Sent.SingleMessage<TenantTestMessage>();
        sentMessage.Id.ShouldBe(msg1.Id);

        // Verify message was received with correct tenant context
        var receivedEnvelope = session.Received.SingleEnvelope<TenantTestMessage>();
        receivedEnvelope.TenantId.ShouldBe("tenant1");
        receivedEnvelope.Message.ShouldBeOfType<TenantTestMessage>().Id.ShouldBe(msg1.Id);
    }

    [Fact]
    public async Task different_tenants_route_to_different_subjects()
    {
        if (_sender == null || _receiver == null)
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }

        var msg1 = new TenantTestMessage(Guid.NewGuid(), "Message for tenant 1");
        var msg2 = new TenantTestMessage(Guid.NewGuid(), "Message for tenant 2");
        
        _output.WriteLine($"Sending message {msg1.Id} with tenant1");
        _output.WriteLine($"Sending message {msg2.Id} with tenant2");

        // Send message for tenant1
        var session1 = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(msg1, new DeliveryOptions { TenantId = "tenant1" });

        var received1 = session1.Received.SingleEnvelope<TenantTestMessage>();
        received1.TenantId.ShouldBe("tenant1");
        received1.Message.ShouldBeOfType<TenantTestMessage>().Id.ShouldBe(msg1.Id);

        // Send message for tenant2
        var session2 = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(msg2, new DeliveryOptions { TenantId = "tenant2" });

        var received2 = session2.Received.SingleEnvelope<TenantTestMessage>();
        received2.TenantId.ShouldBe("tenant2");
        received2.Message.ShouldBeOfType<TenantTestMessage>().Id.ShouldBe(msg2.Id);
    }

    [Fact]
    public async Task fallback_to_default_sends_to_base_subject()
    {
        if (_sender == null || _receiver == null)
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }

        var msgWithTenant = new TenantTestMessage(Guid.NewGuid(), "With tenant");
        var msgWithoutTenant = new TenantTestMessage(Guid.NewGuid(), "Without tenant");

        // Send message with tenant
        var session1 = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(msgWithTenant, new DeliveryOptions { TenantId = "tenant1" });

        var tenantReceived = session1.Received.SingleEnvelope<TenantTestMessage>();
        tenantReceived.TenantId.ShouldBe("tenant1");

        // Send message without tenant (should fallback to default)
        var session2 = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(msgWithoutTenant);

        var defaultReceived = session2.Received.SingleEnvelope<TenantTestMessage>();
        // Without tenant, should receive on base subject without tenant extraction
        defaultReceived.TenantId.ShouldBeNull();
    }

    private async Task<bool> IsNatsAvailable(string natsUrl)
    {
        try
        {
            using var testHost = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseNats(natsUrl);
                })
                .StartAsync();

            await testHost.StopAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Tests for TenantIdRequired behavior
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class TenantIdRequiredBehaviorTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _sender;
    private IHost? _receiver;
    private string _baseSubject = null!;
    private string? _natsUrl;

    public TenantIdRequiredBehaviorTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        _baseSubject = $"test.required.{Guid.NewGuid():N}";

        // Check if NATS is available
        if (!await IsNatsAvailable(_natsUrl))
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }
        
        _sender = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantRequiredSender";
                
                opts.UseNats(_natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.TenantIdRequired)
                    .AddTenant("tenant1");

                opts.PublishMessage<TenantTestMessage>()
                    .ToNatsSubject(_baseSubject);
            })
            .StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "TenantRequiredReceiver";
                
                opts.UseNats(_natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.TenantIdRequired)
                    .AddTenant("tenant1");

                opts.ListenToNatsSubject(_baseSubject);
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_sender != null)
        {
            await _sender.StopAsync();
            _sender.Dispose();
        }
        if (_receiver != null)
        {
            await _receiver.StopAsync();
            _receiver.Dispose();
        }
    }

    [Fact]
    public async Task message_with_tenant_id_is_sent_and_received_successfully()
    {
        if (_sender == null || _receiver == null)
        {
            _output.WriteLine("NATS not available, skipping test");
            return;
        }

        var msg = new TenantTestMessage(Guid.NewGuid(), "With tenant");
        
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(msg, new DeliveryOptions { TenantId = "tenant1" });

        var received = session.Received.SingleEnvelope<TenantTestMessage>();
        received.TenantId.ShouldBe("tenant1");
        received.Message.ShouldBeOfType<TenantTestMessage>().Id.ShouldBe(msg.Id);
    }

    private async Task<bool> IsNatsAvailable(string natsUrl)
    {
        try
        {
            using var testHost = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseNats(natsUrl);
                })
                .StartAsync();

            await testHost.StopAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

#endregion

#region Supporting Types

public record TenantTestMessage(Guid Id, string Content);

public class TenantTestMessageHandler
{
    public void Handle(TenantTestMessage message)
    {
        // Message is handled - the test verifies via tracking
    }
}

#endregion
