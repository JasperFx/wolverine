using System.Diagnostics;
using System.Text;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Wolverine.Util;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusSubscription : AzureServiceBusEndpoint, IBrokerQueue, IMassTransitInteropEndpoint
{
    private bool _hasInitialized;

    public AzureServiceBusSubscription(AzureServiceBusTransport parent, AzureServiceBusTopic topic,
        string subscriptionName) : base(parent,
        new Uri($"{parent.Protocol}://topic/{Uri.EscapeDataString(topic.TopicName)}/{subscriptionName}"),
        EndpointRole.Application)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        SubscriptionName = EndpointName = subscriptionName;
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));

        Options = new CreateSubscriptionOptions(Topic.TopicName, SubscriptionName);

        // default is a simple 1=1 filter
        // This is the same rule as the one used if you
        // use CreateSubscriptionAsync() without specifying a rule
        RuleOptions = new CreateRuleOptions();
    }

    [ChildDescription]
    public CreateSubscriptionOptions Options { get; }

    [ChildDescription]
    public CreateRuleOptions RuleOptions { get; }

    public string SubscriptionName { get; }

    // No attribute needed — AzureServiceBusTopic.ToString() returns TopicName,
    // so this property renders as the topic name string (per audit decision).
    public AzureServiceBusTopic Topic { get; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return Parent.BuildListenerForSubscription(runtime, receiver, this);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
    }

    public override async ValueTask<bool> CheckAsync()
    {
        var exists = true;

        await Parent.WithManagementClientAsync(async client =>
            exists = exists && await client.SubscriptionExistsAsync(Topic.TopicName, SubscriptionName));

        return exists;
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        return new ValueTask(Parent.WithManagementClientAsync(client =>
            client.DeleteSubscriptionAsync(Topic.TopicName, SubscriptionName)));
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        await Parent.WithManagementClientAsync(client => SetupAsync(client, logger).AsTask());
    }

    internal async ValueTask SetupAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        try
        {
            var exists = await client.SubscriptionExistsAsync(Topic.TopicName, SubscriptionName);
            if (!exists)
            {
                Options.SubscriptionName = SubscriptionName;
                Options.TopicName = Topic.TopicName;

                await client.CreateSubscriptionAsync(Options, RuleOptions);
                return;
            }

            // Adjust existing rules to match configuration
            var rules = await client.GetRulesAsync(Topic.TopicName, SubscriptionName).ToListAsync();
            foreach (var rule in rules)
            {
                if (rule.Name == RuleOptions.Name)
                {
                    if (!Equals(rule.Filter, RuleOptions.Filter) || !Equals(rule.Action, RuleOptions.Action))
                    {
                        // Update the rule to match the configuration
                        rule.Filter = RuleOptions.Filter;
                        rule.Action = RuleOptions.Action;

                        await client.UpdateRuleAsync(Topic.TopicName, SubscriptionName, rule);
                    }

                    continue;
                }

                // Unknown rule, delete it
                await client.DeleteRuleAsync(Topic.TopicName, SubscriptionName, rule.Name);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error trying to initialize subscription {Name} to topic {Topic}", SubscriptionName, Topic.TopicName);

            throw;
        }
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        await Parent.WithServiceBusClientAsync(async client =>
        {
            try
            {
                if (Options.RequiresSession)
                {
                    await purgeWithSessions(client);
                }
                else
                {
                    await purgeWithoutSessions(client);
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Error trying to purge Azure Service Bus subscription {SubscriptionName} for topic {TopicName}", SubscriptionName, Topic.TopicName);
            }
        });
    }

    private async Task purgeWithSessions(ServiceBusClient client)
    {
        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(2000);

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        while (stopwatch.ElapsedMilliseconds < 2000)
        {
            var session = await client.AcceptNextSessionAsync(Topic.TopicName, SubscriptionName, cancellationToken: cancellation.Token);

            var messages = await session.ReceiveMessagesAsync(25, 1.Seconds(), cancellation.Token);
            foreach (var message in messages) await session.CompleteMessageAsync(message, cancellation.Token);
            while (messages.Any())
            {
                messages = await session.ReceiveMessagesAsync(25, 1.Seconds(), cancellation.Token);
                foreach (var message in messages) await session.CompleteMessageAsync(message, cancellation.Token);
            }
        }
    }

    private async Task purgeWithoutSessions(ServiceBusClient client)
    {
        var receiver = client.CreateReceiver(Topic.TopicName, SubscriptionName);

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        while (stopwatch.ElapsedMilliseconds < 2000)
        {
            var messages = await receiver.ReceiveMessagesAsync(25, 1.Seconds());
            if (!messages.Any())
            {
                return;
            }

            foreach (var message in messages) await receiver.CompleteMessageAsync(message);
        }
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var dict = new Dictionary<string, string>
        {
            { "TopicName", Topic.TopicName },
            { "SubscriptionName", SubscriptionName }
        };

        await Parent.WithManagementClientAsync(async client =>
        {
            var props = await client.GetSubscriptionAsync(Topic.TopicName, SubscriptionName);
            dict[nameof(SubscriptionProperties.Status)] = props.Value.Status.ToString();
        });
        
        return dict;
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_hasInitialized)
        {
            return;
        }

        await Parent.WithManagementClientAsync(client => InitializeAsync(client, logger).AsTask());

        _hasInitialized = true;
    }

    public override bool IsPartitioned { get => Topic.IsPartitioned; }

    internal async ValueTask InitializeAsync(ServiceBusAdministrationClient client, ILogger logger)
    {
        if (Parent.AutoProvision)
        {
            await SetupAsync(client, logger);
        }

        if (Parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
        }
    }

    internal void UseNServiceBusInterop()
    {
        DefaultSerializer = new NewtonsoftSerializer(new JsonSerializerSettings());
        customizeMapping((m, _) =>
        {
            m.MapPropertyToHeader(x => x.ConversationId, "NServiceBus.ConversationId");
            m.MapPropertyToHeader(x => x.SentAt, "NServiceBus.TimeSent");
            m.MapPropertyToHeader(x => x.CorrelationId!, "NServiceBus.CorrelationId");

            var replyAddress = new Lazy<string>(() =>
            {
                var replyEndpoint = Parent.ReplyEndpoint() as AzureServiceBusQueue;
                return replyEndpoint?.QueueName ?? string.Empty;
            });

            void WriteReplyToAddress(Envelope e, ServiceBusMessage props)
            {
                props.ApplicationProperties["NServiceBus.ReplyToAddress"] = replyAddress.Value;
            }

            void ReadReplyUri(Envelope e, ServiceBusReceivedMessage serviceBusReceivedMessage)
            {
                if (serviceBusReceivedMessage.ApplicationProperties.TryGetValue("NServiceBus.ReplyToAddress",
                        out var raw))
                {
                    var queueName = (raw is byte[] b ? Encoding.UTF8.GetString(b) : raw.ToString())!;
                    e.ReplyUri = new Uri($"{Parent.Protocol}://queue/{queueName}");
                }
            }

            m.MapProperty(x => x.ReplyUri!, ReadReplyUri, WriteReplyToAddress);

            m.MapProperty(x => x.MessageType!, (e, msg) =>
            {
                if (msg.ApplicationProperties.TryGetValue("NServiceBus.EnclosedMessageTypes", out var raw))
                {
                    var typeName = (raw is byte[] b ? Encoding.UTF8.GetString(b) : raw.ToString())!;
                    if (typeName.IsNotEmpty())
                    {
                        var messageType = Type.GetType(typeName);
                        e.MessageType = messageType!.ToMessageTypeName();
                    }
                }
            },
                (e, msg) =>
            {
                msg.ApplicationProperties["NServiceBus.EnclosedMessageTypes"] = e.Message!.GetType().ToMessageTypeName();
            });
        });
    }

    Uri? IMassTransitInteropEndpoint.MassTransitUri()
    {
        return new Uri($"sb://{Parent.HostName}/topic/{Topic.TopicName}/{SubscriptionName}");
    }

    Uri? IMassTransitInteropEndpoint.MassTransitReplyUri()
    {
        return Parent.ReplyEndpoint()!.As<IMassTransitInteropEndpoint>().MassTransitUri();
    }

    Uri? IMassTransitInteropEndpoint.TranslateMassTransitToWolverineUri(Uri uri)
    {
        var lastSegment = uri.Segments.Last();
        return Parent.Queues[lastSegment].Uri;
    }

    internal void UseMassTransitInterop(Action<IMassTransitInterop>? configure = null)
    {
        customizeMapping((m, _) => m.InteropWithMassTransit(configure));
    }
}