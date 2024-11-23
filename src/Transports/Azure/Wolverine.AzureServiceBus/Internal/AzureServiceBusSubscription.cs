using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusSubscription : AzureServiceBusEndpoint, IBrokerQueue
{
    private bool _hasInitialized;

    public AzureServiceBusSubscription(AzureServiceBusTransport parent, AzureServiceBusTopic topic,
        string subscriptionName) : base(parent,
        new Uri($"{parent.Protocol}://topic/{topic.TopicName}/{subscriptionName}"),
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

    public CreateSubscriptionOptions Options { get; }

    public CreateRuleOptions RuleOptions { get; }

    public string SubscriptionName { get; }

    public AzureServiceBusTopic Topic { get; }

    public override Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(CancellationToken cancellationToken)
    {
        return Parent.AcceptNextSessionAsync(this, cancellationToken: cancellationToken);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return Parent.BuildListenerAsync(runtime, receiver, this);
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
        try
        {
            await Parent.WithServiceBusClientAsync(async client =>
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
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error trying to purge Azure Service Bus subscription {SubscriptionName} for topic {TopicName}", SubscriptionName, Topic.TopicName);
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
}