using Azure;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.Internal;

/// <summary>
/// GH-5682. Provisioning a subscription that already exists brings its rules in line with the configured rule: the
/// configured rule is created when it is missing, and every other rule is deleted
/// </summary>
public class subscription_rule_reconciliation_5682
{
    private readonly ServiceBusAdministrationClient theClient = Substitute.For<ServiceBusAdministrationClient>();
    private readonly AzureServiceBusSubscription theSubscription;

    public subscription_rule_reconciliation_5682()
    {
        var transport = new AzureServiceBusTransport();
        theSubscription = new AzureServiceBusSubscription(transport, new AzureServiceBusTopic(transport, "my-topic"), "my-sub");
        theSubscription.RuleOptions.Name = "my-rule";
        theSubscription.RuleOptions.Filter = new CorrelationRuleFilter { Subject = "my.event" };

        theClient.SubscriptionExistsAsync("my-topic", "my-sub", Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(true, Substitute.For<Response>()));
    }

    private void theExistingRulesAre(params RuleProperties[] rules)
    {
        theClient.GetRulesAsync("my-topic", "my-sub", Arg.Any<CancellationToken>())
            .Returns(AsyncPageable<RuleProperties>.FromPages([
                Page<RuleProperties>.FromValues(rules, null, Substitute.For<Response>())
            ]));
    }

    [Fact]
    public async Task the_configured_rule_is_created_on_a_subscription_with_no_rules()
    {
        theExistingRulesAre();

        await theSubscription.SetupAsync(theClient, NullLogger.Instance);

        await theClient.Received(1).CreateRuleAsync("my-topic", "my-sub",
            Arg.Is<CreateRuleOptions>(x => x.Name == "my-rule" && x.Filter.Equals(new CorrelationRuleFilter { Subject = "my.event" })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task the_configured_rule_replaces_a_rule_under_another_name()
    {
        theExistingRulesAre(ServiceBusModelFactory.RuleProperties("$Default", new TrueRuleFilter()));

        await theSubscription.SetupAsync(theClient, NullLogger.Instance);

        await theClient.Received(1).CreateRuleAsync("my-topic", "my-sub",
            Arg.Is<CreateRuleOptions>(x => x.Name == "my-rule"), Arg.Any<CancellationToken>());
        await theClient.Received(1).DeleteRuleAsync("my-topic", "my-sub", "$Default", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task an_existing_configured_rule_is_not_created_again()
    {
        theExistingRulesAre(ServiceBusModelFactory.RuleProperties("my-rule", new CorrelationRuleFilter { Subject = "my.event" }));

        await theSubscription.SetupAsync(theClient, NullLogger.Instance);

        await theClient.DidNotReceive().CreateRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CreateRuleOptions>(),
            Arg.Any<CancellationToken>());
        await theClient.DidNotReceive().DeleteRuleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
