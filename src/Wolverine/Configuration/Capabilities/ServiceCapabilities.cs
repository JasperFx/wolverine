using System.Reflection;
using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Configuration.Capabilities;

public class ServiceCapabilities : OptionsDescription
{
    [JsonConstructor]
    public ServiceCapabilities()
    {
    }

    public ServiceCapabilities(WolverineOptions options) : base(options)
    {
        Version = (options.ApplicationAssembly ?? Assembly.GetEntryAssembly()).GetName().Version;
        WolverineVersion = options.GetType().Assembly.GetName().Version;
    }

    public DateTimeOffset Evaluated { get; set; } = DateTimeOffset.UtcNow;
    public Version Version { get; set; }

    public Version? WolverineVersion { get; set; }

    public List<EventStoreUsage> EventStores { get; set; } = new();

    public List<MessageDescriptor> Messages { get; set; } = new();

    public List<MessageStore> MessageStores { get; set; } = new();

    public List<EndpointDescriptor> MessagingEndpoints { get; set; } = new();

    public DatabaseCardinality MessageStoreCardinality { get; set; } = DatabaseCardinality.None;

    /// <summary>
    ///     Uri for sending command messages to this service
    /// </summary>
    public Uri? SystemControlUri { get; set; }

    public static async Task<ServiceCapabilities> ReadFrom(IWolverineRuntime runtime, Uri? systemControlUri,
        CancellationToken token)
    {
        var capabilities = new ServiceCapabilities(runtime.Options);
        capabilities.SystemControlUri = systemControlUri;

        var collection = runtime.Stores;
        var stores = await collection.FindAllAsync();
        foreach (var store in stores)
            capabilities.MessageStores.Add(new MessageStore(store.Uri, true, store.Describe()));

        capabilities.MessageStoreCardinality = collection.Cardinality();

        var eventStores = runtime.Services.GetServices<IEventStore>();
        foreach (var eventStore in eventStores)
        {
            var eventStoreUsage = await eventStore.TryCreateUsage(token);
            if (eventStoreUsage != null)
            {
                capabilities.EventStores.Add(eventStoreUsage);
            }
        }

        var messageTypes = runtime.Options.Discovery.FindAllMessages(runtime.Options.HandlerGraph);
        foreach (var messageType in messageTypes)
            capabilities.Messages.Add(new MessageDescriptor(messageType, runtime));

        foreach (var endpoint in runtime.Options.Transports.AllEndpoints())
            capabilities.MessagingEndpoints.Add(new EndpointDescriptor(endpoint));

        return capabilities;
    }
}