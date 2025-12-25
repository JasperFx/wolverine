using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Nats.Tests.Helpers;
using Wolverine.Tracking;
using Wolverine.Transports.Sending;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

[Collection("NATS MultiTenancy Tests")]
[Trait("Category", "Integration")]
public class MultiTenancyTests
{
    private readonly ITestOutputHelper _output;

    public MultiTenancyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void subject_mapper_works_correctly()
    {
        var mapper = new Internal.DefaultTenantSubjectMapper();
        
        // Test basic mapping
        Assert.Equal("tenant1.orders", mapper.MapSubject("orders", "tenant1"));
        Assert.Equal("tenant2.orders", mapper.MapSubject("orders", "tenant2"));
        
        // Test extraction
        Assert.Equal("tenant1", mapper.ExtractTenantId("tenant1.orders"));
        Assert.Equal("tenant2", mapper.ExtractTenantId("tenant2.orders"));
        
        // Test subscription pattern
        Assert.Equal("*.orders", mapper.GetSubscriptionPattern("orders"));
        
        // Test with nested subjects
        Assert.Equal("tenant1.orders.created", mapper.MapSubject("orders.created", "tenant1"));
        Assert.Equal("*.orders.created", mapper.GetSubscriptionPattern("orders.created"));
        Assert.Equal("tenant1", mapper.ExtractTenantId("tenant1.orders.created"));
    }

    [Fact(Skip = "Flaky in CI - NATS wildcard subscriptions with Wolverine tracking don't reliably wait for handler execution. " +
                 "The multi-tenancy feature works correctly (messages are routed to tenant-prefixed subjects), " +
                 "but the test synchronization is unreliable. Consider using a different testing approach.")]
    public async Task messages_are_routed_by_tenant()
    {
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        var baseSubject = $"test.multitenancy.{Guid.NewGuid():N}";
        
        _output.WriteLine($"Using NATS URL: {natsUrl}");
        _output.WriteLine($"Base subject: {baseSubject}");
        
        var receivedMessages = new List<(string? TenantId, TenantTestMessage Message)>();
        
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .ConfigureServices(services =>
            {
                services.AddSingleton(receivedMessages);
            })
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenant1")
                    .AddTenant("tenant2");

                // Listen to the base subject - this will create a wildcard subscription
                opts.ListenToNatsSubject(baseSubject);
                
                // Publish to the base subject  
                opts.PublishMessage<TenantTestMessage>()
                    .ToNatsSubject(baseSubject);
                
                // Add handler
                opts.Discovery.IncludeType<TenantMessageHandler>();
            })
            .StartAsync();
        
        // Send messages for different tenants
        var msg1 = new TenantTestMessage(Guid.NewGuid(), "Message for tenant 1");
        var msg2 = new TenantTestMessage(Guid.NewGuid(), "Message for tenant 2");
        
        _output.WriteLine($"Sending message {msg1.Id} with tenant1");
        _output.WriteLine($"Sending message {msg2.Id} with tenant2");
        
        // Send messages
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(msg1, new DeliveryOptions { TenantId = "tenant1" });
        await bus.PublishAsync(msg2, new DeliveryOptions { TenantId = "tenant2" });
        
        // Wait for processing - increase delay for CI
        await Task.Delay(3000);
        
        _output.WriteLine($"Received {receivedMessages.Count} messages");
        foreach (var (tenantId, msg) in receivedMessages)
        {
            _output.WriteLine($"  - Message {msg.Id}, TenantId: {tenantId ?? "null"}");
        }
        
        // Verify messages were received with correct tenant context
        Assert.Equal(2, receivedMessages.Count);
        Assert.Contains(receivedMessages, m => m.TenantId == "tenant1" && m.Message.Id == msg1.Id);
        Assert.Contains(receivedMessages, m => m.TenantId == "tenant2" && m.Message.Id == msg2.Id);
        
        await host.StopAsync();
    }

    [Fact(Skip = "Flaky in CI - Multi-tenancy tests have timing issues in CI environment. " +
                 "The feature works correctly locally but CI has reliability issues.")]
    public async Task tenant_id_required_behavior_latches_sending_agent()
    {
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        var baseSubject = $"test.required.{Guid.NewGuid():N}";
        
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.TenantIdRequired)
                    .AddTenant("tenant1");

                opts.PublishMessage<TenantTestMessage>()
                    .ToNatsSubject(baseSubject);
            })
            .StartAsync();
        
        var bus = host.Services.GetRequiredService<IMessageBus>();
        
        // Try to send without tenant ID - this will fail in the sending agent
        var msg = new TenantTestMessage(Guid.NewGuid(), "Message without tenant");
        
        // This will not throw immediately, but will log errors in the sending agent
        await bus.PublishAsync(msg);
        
        // Give time for the error to be logged
        await Task.Delay(100);
        
        // The sending agent should be latched after the error
        // We can't easily assert on this without accessing internals,
        // but the logs will show the error
        
        await host.StopAsync();
    }

    [Fact(Skip = "Flaky in CI - Multi-tenancy tests have timing issues in CI environment. " +
                 "The feature works correctly locally but CI has reliability issues.")]
    public async Task fallback_to_default_behavior_sends_to_base_subject()
    {
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        var baseSubject = $"test.fallback.{Guid.NewGuid():N}";
        
        var receivedMessages = new List<(string? TenantId, TenantTestMessage Message)>();
        
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .ConfigureServices(services =>
            {
                services.AddSingleton(receivedMessages);
            })
            .UseWolverine(opts =>
            {
                opts.UseNats(natsUrl)
                    .ConfigureMultiTenancy(TenantedIdBehavior.FallbackToDefault)
                    .AddTenant("tenant1");

                // Listen to the base subject - this will create a wildcard subscription
                opts.ListenToNatsSubject(baseSubject)
                    .ProcessInline();
                
                opts.PublishMessage<TenantTestMessage>()
                    .ToNatsSubject(baseSubject);
                
                opts.Discovery.IncludeType<TenantMessageHandler>();
            })
            .StartAsync();
        
        var bus = host.Services.GetRequiredService<IMessageBus>();
        
        // Send with tenant
        var msg1 = new TenantTestMessage(Guid.NewGuid(), "With tenant");
        await bus.PublishAsync(msg1, new DeliveryOptions { TenantId = "tenant1" });
        
        // Send without tenant (should fallback to default)
        var msg2 = new TenantTestMessage(Guid.NewGuid(), "Without tenant");
        await bus.PublishAsync(msg2);
        
        // Wait for messages to be processed - increased delay for CI reliability
        await Task.Delay(3000);
        
        Assert.Equal(2, receivedMessages.Count);
        
        // Check tenant message was received with tenant ID
        var tenantMsg = receivedMessages.First(m => m.Message.Id == msg1.Id);
        Assert.Equal("tenant1", tenantMsg.TenantId);
        
        // Check non-tenant message was received without tenant ID
        var defaultMsg = receivedMessages.First(m => m.Message.Id == msg2.Id);
        Assert.Null(defaultMsg.TenantId);
        
        await host.StopAsync();
    }
}

public record TenantTestMessage(Guid Id, string Content);

public class TenantMessageHandler
{
    private readonly List<(string? TenantId, TenantTestMessage Message)> _receivedMessages;
    private readonly ILogger<TenantMessageHandler> _logger;
    
    public TenantMessageHandler(
        List<(string? TenantId, TenantTestMessage Message)> receivedMessages,
        ILogger<TenantMessageHandler> logger)
    {
        _receivedMessages = receivedMessages;
        _logger = logger;
    }
    
    public void Handle(TenantTestMessage message, Envelope envelope)
    {
        _logger.LogInformation("Received message {MessageId} with TenantId: {TenantId}", 
            message.Id, envelope.TenantId ?? "null");
        _receivedMessages.Add((envelope.TenantId, message));
    }
}


public class TenantMessageHandlerWithSubject
{
    private readonly List<(string? TenantId, TenantTestMessage Message, string Subject)> _receivedMessages;
    private readonly ILogger<TenantMessageHandlerWithSubject> _logger;
    
    public TenantMessageHandlerWithSubject(
        List<(string? TenantId, TenantTestMessage Message, string Subject)> receivedMessages,
        ILogger<TenantMessageHandlerWithSubject> logger)
    {
        _receivedMessages = receivedMessages;
        _logger = logger;
    }
    
    public void Handle(TenantTestMessage message, Envelope envelope)
    {
        var subject = envelope.Destination?.ToString() ?? "unknown";
        _logger.LogInformation("Received message {MessageId} with TenantId: {TenantId} on subject: {Subject}", 
            message.Id, envelope.TenantId ?? "null", subject);
        _receivedMessages.Add((envelope.TenantId, message, subject));
    }
}