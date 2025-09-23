using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using JasperFx.Resources;
using Wolverine.Util;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Redis.Tests;

public class DocumentationSamples
{
    public static async Task configure()
    {
        #region sample_bootstrapping_with_redis

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379")
                    
                    // Auto-create streams and consumer groups
                    .AutoProvision()
                    
                    // Configure default consumer name selector for all Redis listeners
                    .ConfigureDefaultConsumerName((runtime, endpoint) => 
                        $"{runtime.Options.ServiceName}-{runtime.DurabilitySettings.AssignedNodeNumber}")
                    
                    // Useful for testing - auto purge queues on startup
                    .AutoPurgeOnStartup();

                // Just publish all messages to Redis streams (uses database 0 by default)
                opts.PublishAllMessages().ToRedisStream("wolverine-messages");

                // Or explicitly configure message routing with database ID
                opts.PublishMessage<ColorMessage>()
                    .ToRedisStream("colors", databaseId: 1)
                    
                    // Configure specific settings for this stream
                    .BatchSize(50)
                    .Inline();

                // Listen to Redis streams with consumer groups (uses database 0 by default)
                opts.ListenToRedisStream("red", "color-processors")
                    .Inline()
                    
                    // Configure consumer settings
                    .ConsumerName("red-consumer-1")
                    .BatchSize(10)
                    .BlockTimeout(TimeSpan.FromSeconds(5))
                    
                    // Start from beginning to consume existing messages (like Kafka's AutoOffsetReset.Earliest)
                    .StartFromBeginning();

                // Listen to Redis streams with database ID specified
                opts.ListenToRedisStream("green", "color-processors", databaseId: 2)
                    .BufferedInMemory()
                    .BatchSize(25)
                    .StartFromNewMessages(); // Default: only new messages (like Kafka's AutoOffsetReset.Latest)

                opts.ListenToRedisStream("blue", "color-processors", databaseId: 3)
                    .Durable()
                    .ConsumerName("blue-consumer")
                    .StartFromBeginning(); // Process existing messages too
                    
                // Alternative: use StartFrom parameter directly
                opts.ListenToRedisStream("purple", "color-processors", StartFrom.Beginning)
                    .BufferedInMemory();

                // This will direct Wolverine to try to ensure that all
                // referenced Redis streams and consumer groups exist at 
                // application start up time
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_with_database_ids()
    {
        #region sample_redis_database_configuration

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379");

                // Configure streams on different databases
                opts.PublishMessage<OrderCreated>()
                    .ToRedisStream("orders", databaseId: 1);
                    
                opts.PublishMessage<PaymentProcessed>()
                    .ToRedisStream("payments", databaseId: 2);

                // Listen on different databases
                opts.ListenToRedisStream("orders", "order-processors", databaseId: 1);
                opts.ListenToRedisStream("payments", "payment-processors", databaseId: 2);
                
                // Advanced configuration with database ID
                opts.ListenToRedisStream("notifications", "notification-processors", databaseId: 3, endpoint =>
                {
                    endpoint.ConsumerName("notification-consumer-1");
                    endpoint.BatchSize(100);
                    endpoint.BlockTimeout(TimeSpan.FromSeconds(10));
                    endpoint.Durable();
                });
            }).StartAsync();

        #endregion
    }

    public static async Task configure_with_uri_helpers()
    {
        #region sample_redis_uri_helpers

        // Using URI builder helpers
        var ordersUri = RedisStreamEndpointExtensions.BuildRedisStreamUri("orders", databaseId: 1);
        var paymentsUri = RedisStreamEndpointExtensions.BuildRedisStreamUri("payments", databaseId: 2, "payment-processors");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379");
                
                // Configure endpoints to listen and publish
                opts.ListenToRedisStream("orders", "order-processors", databaseId: 1);
                opts.ListenToRedisStream("payments", "payment-processors", databaseId: 2);
            }).StartAsync();

        // Send directly to specific database URIs
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.EndpointFor(ordersUri).SendAsync(new OrderCreated("123", 99.99m, DateTime.Now));

        #endregion
    }

    public static async Task working_with_multiple_databases()
    {
        #region sample_multiple_database_usage

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision();

                // Different message types on different databases for isolation
                
                // Database 0: Default messages
                opts.PublishMessage<SystemEvent>().ToRedisStream("system-events");
                opts.ListenToRedisStream("system-events", "system-processors");
                
                // Database 1: Order processing
                opts.PublishMessage<OrderCreated>().ToRedisStream("orders", 1);
                opts.ListenToRedisStream("orders", "order-processors", 1);
                
                // Database 2: Payment processing  
                opts.PublishMessage<PaymentProcessed>().ToRedisStream("payments", 2);
                opts.ListenToRedisStream("payments", "payment-processors", 2);
                
                // Database 3: Analytics and reporting
                opts.PublishMessage<AnalyticsEvent>().ToRedisStream("analytics", 3);
                opts.ListenToRedisStream("analytics", "analytics-processors", 3);
            }).StartAsync();

        #endregion
    }
}

#region sample_RedisInstrumentation_middleware

public static class RedisInstrumentation
{
    // Just showing what data elements are available to use for 
    // extra instrumentation when listening to Redis streams
    public static void Before(Envelope envelope, ILogger logger)
    {
        logger.LogDebug("Received message from Redis stream {StreamKey} with Id={MessageId} and Database={DatabaseId}", 
            envelope.TopicName, envelope.Id, envelope.Headers.GetValueOrDefault("DatabaseId"));
    }
}

#endregion

#region sample_OurRedisJsonMapper

// Simplistic envelope mapper that expects every message to be of
// type "T" and serialized as JSON that works perfectly well w/ our
// application's default JSON serialization
public class OurRedisJsonMapper<TMessage> : EnvelopeMapper<StreamEntry, List<NameValueEntry>>, IRedisEnvelopeMapper
{
    // Wolverine needs to know the message type name
    private readonly string _messageTypeName = typeof(TMessage).ToMessageTypeName();

    public OurRedisJsonMapper(Endpoint endpoint) : base(endpoint)
    {
        // Map the data property
        MapProperty(x => x.Data!, 
            (e, m) => e.Data = m.Values.FirstOrDefault(x => x.Name == "data").Value,
            (e, m) => m.Add(new NameValueEntry("data", e.Data)));
        
        // Set up the message type
        MapProperty(x => x.MessageType!,
            (e, m) => e.MessageType = _messageTypeName,
            (e, m) => m.Add(new NameValueEntry("message-type", _messageTypeName)));
        
        // Set up content type    
        MapProperty(x => x.ContentType!,
            (e, m) => e.ContentType = "application/json",
            (e, m) => m.Add(new NameValueEntry("content-type", "application/json")));
    }

    protected override void writeOutgoingHeader(List<NameValueEntry> outgoing, string key, string value)
    {
        outgoing.Add(new NameValueEntry($"header-{key}", value));
    }

    protected override bool tryReadIncomingHeader(StreamEntry incoming, string key, out string? value)
    {
        var target = $"header-{key}";
        foreach (var nv in incoming.Values)
        {
            if (nv.Name.Equals(target))
            {
                value = nv.Value.ToString();
                return true;
            }
        }

        value = null;
        return false;
    }

    protected override void writeIncomingHeaders(StreamEntry incoming, Envelope envelope)
    {
        var headers = incoming.Values.Where(k => k.Name.StartsWith("header-"));
        foreach (var nv in headers)
        {
            envelope.Headers[nv.Name.ToString()[7..]] = nv.Value.ToString(); // Remove "header-" prefix
        }

        // Capture the Redis stream message id
        envelope.Headers["redis-entry-id"] = incoming.Id.ToString();
    }
}

#endregion

// Sample message types for documentation
public record ColorMessage(string Color, DateTime Timestamp);
public record OrderCreated(string OrderId, decimal Amount, DateTime CreatedAt);
public record PaymentProcessed(string PaymentId, string OrderId, decimal Amount);
public record SystemEvent(string EventType, string Description, DateTime Timestamp);
public record AnalyticsEvent(string EventName, Dictionary<string, object> Properties, DateTime Timestamp);
