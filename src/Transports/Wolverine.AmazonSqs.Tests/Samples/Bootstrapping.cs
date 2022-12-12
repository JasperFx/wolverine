using Amazon.Runtime;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;

namespace Wolverine.AmazonSqs.Tests.Samples;

public class Bootstrapping
{
    public async Task for_local_development()
    {
        #region sample_connect_to_sqs_and_localstack

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Connect to an SQS broker running locally
                // through LocalStack
                opts.UseAmazonSqsTransportLocally();
            }).StartAsync();

        #endregion
    }

    public async Task connect_to_broker()
    {
        #region sample_simplistic_aws_sqs_setup

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport()

                    // Let Wolverine create missing queues as necessary
                    .AutoProvision()

                    // Optionally purge all queues on application startup. 
                    // Warning though, this is potentially slow
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public async Task connect_with_customization()
    {
        #region sample_config_aws_sqs_connection

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                var config = context.Configuration;

                opts.UseAmazonSqsTransport(sqsConfig =>
                    {
                        sqsConfig.ServiceURL = config["AwsUrl"];
                        // And any other elements of the SQS AmazonSQSConfig
                        // that you may need to configure
                    })

                    // Let Wolverine create missing queues as necessary
                    .AutoProvision()

                    // Optionally purge all queues on application startup. 
                    // Warning though, this is potentially slow
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public async Task setting_credentials()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                var config = context.Configuration;

                opts.UseAmazonSqsTransport(sqsConfig =>
                    {
                        sqsConfig.ServiceURL = config["AwsUrl"];
                        // And any other elements of the SQS AmazonSQSConfig
                        // that you may need to configure
                    })

                    // And you can also add explicit AWS credentials 
                    .Credentials(new BasicAWSCredentials(config["AwsAccessKey"], config["AwsSecretKey"]))

                    // Let Wolverine create missing queues as necessary
                    .AutoProvision()

                    // Optionally purge all queues on application startup. 
                    // Warning though, this is potentially slow
                    .AutoPurgeOnStartup();
            }).StartAsync();
    }

    public async Task configuring_queues()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport()

                    // Let Wolverine create missing queues as necessary
                    .AutoProvision()

                    // Optionally purge all queues on application startup. 
                    // Warning though, this is potentially slow
                    .AutoPurgeOnStartup();

                opts.ListenToSqsQueue("incoming", queue =>
                {
                    queue.Configuration.Attributes[QueueAttributeName.DelaySeconds]
                        = "5";

                    queue.Configuration.Attributes[QueueAttributeName.MessageRetentionPeriod]
                        = 4.Days().TotalSeconds.ToString();
                });
            }).StartAsync();
    }

    public async Task using_conventional_routing()
    {
        #region sample_using_conventional_sqs_routing

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport()
                    .UseConventionalRouting();

            }).StartAsync();

        #endregion
    }
}