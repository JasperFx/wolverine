using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSns.Tests.Samples;

public class Bootstrapping
{
    public async Task use_named_brokers()
    {
        #region sample_using_multiple_sns_brokers

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The "default" broker
                opts.UseAmazonSnsTransport();

                // Add a second, named Amazon SNS broker that connects to a
                // different account or region
                opts.AddNamedAmazonSnsBroker(new BrokerName("americas"), snsConfig =>
                {
                    snsConfig.RegionEndpoint = RegionEndpoint.USEast1;
                }).AutoProvision();

                // Publish to a topic on the named broker. The Uri scheme for these
                // endpoints is the broker name, so you'd see "americas://colors".
                opts.PublishMessage<Message1>()
                    .ToSnsTopicOnNamedBroker(new BrokerName("americas"), "colors");
            }).StartAsync();

        #endregion
    }

    public async Task broker_per_tenant()
    {
        #region sample_sns_broker_per_tenant

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The "default" / shared SNS connection
                opts.UseAmazonSnsTransport(config =>
                {
                    config.RegionEndpoint = RegionEndpoint.USEast1;
                })
                    .AutoProvision()

                    // How should Wolverine route a message whose TenantId is null or
                    // unknown? FallbackToDefault (the default) uses the shared connection;
                    // TenantIdRequired throws; IgnoreUnknownTenants silently drops it.
                    .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

                    // Each tenant gets its OWN dedicated SNS connection, but shares the
                    // topic topology declared below. This tenant inherits the parent's
                    // AWS credentials, and just re-points at its own region.
                    .AddTenant("tenant-west", config =>
                    {
                        config.RegionEndpoint = RegionEndpoint.USWest2;
                    })

                    // Or give the tenant its own dedicated AWS account by supplying
                    // its own credentials (optionally with its own region/endpoint too):
                    .AddTenant("tenant-eu", new BasicAWSCredentials("tenant-eu-key", "tenant-eu-secret"),
                        config =>
                        {
                            config.RegionEndpoint = RegionEndpoint.EUWest1;
                        });

                // SNS is publish-only, so broker-per-tenant means tenant-specific PUBLISHERS.
                // A message is published to the right tenant's connection at runtime by its
                // Envelope.TenantId (e.g. new DeliveryOptions { TenantId = "tenant-west" }).
                opts.PublishMessage<Message1>().ToSnsTopic("colors");
            }).StartAsync();

        #endregion
    }

    public async Task for_local_development()
    {
        #region sample_connect_to_sns_and_localstack
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Connect to an SNS broker running locally
                // through LocalStack
                opts.UseAmazonSnsTransportLocally();
            }).StartAsync();

        #endregion
    }

    public async Task connect_to_broker()
    {
        #region sample_simplistic_aws_sns_setup
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This does depend on the server having an AWS credentials file
                // See https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html for more information
                opts.UseAmazonSnsTransport()

                    // Let Wolverine create missing topics and subscriptions as necessary
                    .AutoProvision();
            }).StartAsync();

        #endregion
    }
    
    public async Task connect_with_customization()
    {
        #region sample_config_aws_sns_connection
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var config = builder.Configuration;

            opts.UseAmazonSnsTransport(snsConfig =>
                {
                    snsConfig.ServiceURL = config["AwsUrl"];
                    // And any other elements of the SNS AmazonSimpleNotificationServiceConfig
                    // that you may need to configure
                })

                // Let Wolverine create missing topics and subscriptions as necessary
                .AutoProvision();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }
    
    public async Task setting_credentials()
    {
        #region sample_setting_aws_sns_credentials
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var config = builder.Configuration;

            opts.UseAmazonSnsTransport(snsConfig =>
                {
                    snsConfig.ServiceURL = config["AwsUrl"];
                    // And any other elements of the SNS AmazonSimpleNotificationServiceConfig
                    // that you may need to configure
                })

                // And you can also add explicit AWS credentials
                .Credentials(new BasicAWSCredentials(config["AwsAccessKey"], config["AwsSecretKey"]))

                // Let Wolverine create missing topics and subscriptions as necessary
                .AutoProvision();
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }
    
    public async Task publishing()
    {
        #region sample_subscriber_rules_for_sns
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransport();

                opts.PublishMessage<Message1>()
                    .ToSnsTopic("outbound1")

                    // Increase the outgoing message throughput, but at the cost
                    // of strict ordering
                    .MessageBatchMaxDegreeOfParallelism(Environment.ProcessorCount)
                    .ConfigureTopicCreation(conf =>
                    {
                        // Configure topic creation request...
                    });
            }).StartAsync();

        #endregion
    }
    
    public async Task topic_subscriptions()
    {
        #region sample_sns_topic_subscriptions_creation
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSnsTransport()
                    // Without this, the SubscribeSqsQueue() call does nothing
                    // This tells Wolverine to try to ensure all topics, subscriptions,
                    // and SQS queues exist at runtime 
                    .AutoProvision()
                    
                    // *IF* you need to use some kind of custom queue policy in your
                    // SQS queues *and* want to use AutoProvision() as well, this is 
                    // the hook to customize that policy. This is the default though that
                    // we're just showing for an example
                    .QueuePolicyForSqsSubscriptions(description =>
                    {
                        return $$"""
                                 {
                                   "Version": "2012-10-17",
                                   "Statement": [{
                                       "Effect": "Allow",
                                       "Principal": {
                                           "Service": "sns.amazonaws.com"
                                       },
                                       "Action": "sqs:SendMessage",
                                       "Resource": "{{description.QueueArn}}",
                                       "Condition": {
                                         "ArnEquals": {
                                             "aws:SourceArn": "{{description.TopicArn}}"
                                         }
                                       }
                                   }]
                                 }
                                 """;
                    });

                opts.PublishMessage<Message1>()
                    .ToSnsTopic("outbound1")
                    // Sets a subscriptions to be
                    .SubscribeSqsQueue("queueName",
                        config =>
                        {
                            // Configure subscription attributes
                            config.RawMessageDelivery = true;
                        });
            }).StartAsync();

        #endregion
    }
    
}
