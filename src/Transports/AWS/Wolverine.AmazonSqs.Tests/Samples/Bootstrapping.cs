using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using TestingSupport.Compliance;

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
                // This does depend on the server having an AWS credentials file
                // See https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-files.html for more information
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
        #region sample_setting_aws_credentials

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

        #endregion
    }

    public async Task configuring_queues()
    {
        #region sample_listen_to_sqs_queue

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
                })
                    // You can optimize the throughput by running multiple listeners
                    // in parallel
                    .ListenerCount(5);
            }).StartAsync();

        #endregion
    }

    public async Task publishing()
    {
        #region sample_subscriber_rules_for_sqs

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport();

                opts.PublishMessage<Message1>()
                    .ToSqsQueue("outbound1")

                    // Increase the outgoing message throughput, but at the cost
                    // of strict ordering
                    .MessageBatchMaxDegreeOfParallelism(Environment.ProcessorCount);


                opts.PublishMessage<Message2>()
                    .ToSqsQueue("outbound2").ConfigureQueueCreation(request =>
                    {
                        request.Attributes[QueueAttributeName.MaximumMessageSize] = "1024";
                    });
            }).StartAsync();

        #endregion
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

    public async Task overriding_dead_letter_queueing()
    {
        #region sample_configuring_dead_letter_queue_for_sqs

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport();

                // No dead letter queueing
                opts.ListenToSqsQueue("incoming")
                    .DisableDeadLetterQueueing();

                // Use a different dead letter queue
                opts.ListenToSqsQueue("important")
                    .ConfigureDeadLetterQueue("important_errors", q =>
                    {
                        // optionally configure how the dead letter queue itself
                        // is built by Wolverine
                        q.MaxNumberOfMessages = 1000;
                    });
            }).StartAsync();

        #endregion
    }

    public async Task receive_raw_json()
    {
        #region sample_receive_raw_json_in_sqs

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport();

                opts.ListenToSqsQueue("incoming").ReceiveRawJsonMessage(
                    // Specify the single message type for this queue
                    typeof(Message1),

                    // Optionally customize System.Text.Json configuration
                    o =>
                    {
                        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    });
            }).StartAsync();

        #endregion
    }

    public async Task publish_raw_json()
    {
        #region sample_publish_raw_json_in_sqs

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport();

                opts.PublishAllMessages().ToSqsQueue("outgoing").SendRawJsonMessage(
                    // Specify the single message type for this queue
                    typeof(Message1),

                    // Optionally customize System.Text.Json configuration
                    o =>
                    {
                        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    });
            }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task customize_mappers()
    {
        #region sample_apply_custom_sqs_mapping

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport()
                    .UseConventionalRouting()

                    .ConfigureListeners(l => l.InteropWith(new CustomSqsMapper()))

                    .ConfigureSenders(s => s.InteropWith(new CustomSqsMapper()));

            }).StartAsync();

        #endregion
    }
}

#region sample_custom_sqs_mapper

public class CustomSqsMapper : ISqsEnvelopeMapper
{
    public string BuildMessageBody(Envelope envelope)
    {
        // Serialized data from the Wolverine message
        return Encoding.Default.GetString(envelope.Data);
    }

    // Specify header values for the SQS message from the Wolverine envelope
    public IEnumerable<KeyValuePair<string, MessageAttributeValue>> ToAttributes(Envelope envelope)
    {
        if (envelope.TenantId.IsNotEmpty())
        {
            yield return new KeyValuePair<string, MessageAttributeValue>("tenant-id", new MessageAttributeValue{StringValue = envelope.TenantId});
        }
    }

    public void ReadEnvelopeData(Envelope envelope, string messageBody, IDictionary<string, MessageAttributeValue> attributes)
    {
        envelope.Data = Encoding.Default.GetBytes(messageBody);

        if (attributes.TryGetValue("tenant-id", out var att))
        {
            envelope.TenantId = att.StringValue;
        }
    }
}

#endregion