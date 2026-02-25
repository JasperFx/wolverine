using System.Reflection;
using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Wolverine.Attributes;
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

    [JsonConverter(typeof(VersionJsonConverter))]
    public Version Version { get; set; }

    [JsonConverter(typeof(VersionJsonConverter))]
    public Version? WolverineVersion { get; set; }

    public List<EventStoreUsage> EventStores { get; set; } = [];

    public List<MessageDescriptor> Messages { get; set; } = [];

    public List<MessageStore> MessageStores { get; set; } = [];

    public List<EndpointDescriptor> MessagingEndpoints { get; set; } = [];

    public DatabaseCardinality MessageStoreCardinality { get; set; } = DatabaseCardinality.None;

    public List<BrokerDescription> Brokers { get; set; } = [];

    /// <summary>
    ///     Uri for sending command messages to this service
    /// </summary>
    public Uri? SystemControlUri { get; set; }

    public static async Task<ServiceCapabilities> ReadFrom(IWolverineRuntime runtime, Uri? systemControlUri,
        CancellationToken token)
    {
        var capabilities = new ServiceCapabilities(runtime.Options)
        {
            SystemControlUri = systemControlUri
        };

        readTransports(runtime, capabilities);

        await readMessageStores(runtime, capabilities);

        await readEventStores(runtime, token, capabilities);

        readMessageTypes(runtime, capabilities);

        readEndpoints(runtime, capabilities);

        return capabilities;
    }

    private static void readEndpoints(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        foreach (var endpoint in runtime.Options.Transports.AllEndpoints().OrderBy(x => x.Uri.ToString()))
        {
            if (endpoint.Role == EndpointRole.System) continue;
            capabilities.MessagingEndpoints.Add(new EndpointDescriptor(endpoint));
        }
    }

    private static void readMessageTypes(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var messageTypes = runtime.Options.Discovery.FindAllMessages(runtime.Options.HandlerGraph);
        foreach (var messageType in messageTypes.OrderBy(x => x.FullNameInCode()))
        {
            if (messageType.Assembly.HasAttribute<ExcludeFromServiceCapabilitiesAttribute>()) continue;
            capabilities.Messages.Add(new MessageDescriptor(messageType, runtime));
        }
    }

    private static async Task readEventStores(IWolverineRuntime runtime, CancellationToken token,
        ServiceCapabilities capabilities)
    {
        var eventStores = runtime.Services.GetServices<IEventStore>();
        var storeList = new List<EventStoreUsage>();
        foreach (var eventStore in eventStores)
        {
            var eventStoreUsage = await eventStore.TryCreateUsage(token);
            if (eventStoreUsage != null)
            {
                storeList.Add(eventStoreUsage);
            }
        }
        
        capabilities.EventStores.AddRange(storeList.OrderBy(x => x.SubjectUri.ToString()));
    }

    private static async Task readMessageStores(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        var collection = runtime.Stores;
        var stores = await collection.FindAllAsync();
        capabilities.MessageStores.AddRange(stores.Select(MessageStore.For).OrderBy(x => x.Uri.ToString()));

        capabilities.MessageStoreCardinality = collection.Cardinality();
    }

    private static void readTransports(IWolverineRuntime runtime, ServiceCapabilities capabilities)
    {
        foreach (var transport in runtime.Options.Transports)
        {
            if (transport.TryBuildBrokerUsage(out var usage))
            {
                capabilities.Brokers.Add(usage);
            }
        }
    }
}