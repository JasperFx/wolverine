using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSns.Internal;
using Wolverine.AmazonSqs;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
/// GH-5126. <c>UseAmazonSnsTransportUsingNamedConnection(name)</c> /
/// <c>AddNamedAmazonSnsBrokerUsingNamedConnection(broker, name)</c> read the service URL of a local AWS emulator
/// from <c>ConnectionStrings:{name}</c>, which is where a .NET Aspire AppHost's <c>builder.AddLocalStack("name")</c>
/// (LocalStack.Aspire.Hosting) or <c>builder.AddFlociAws("name")</c> (CommunityToolkit) + <c>.WithReference(...)</c>
/// puts it, as <c>http://localhost:{port}</c>. Only the end-to-end tests need LocalStack.
/// </summary>
public class named_connection_5126
{
    private static IHostBuilder hostWith(Action<WolverineOptions> configure,
        params (string Name, string Value)[] connectionStrings)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                connectionStrings.Select(x =>
                    new KeyValuePair<string, string?>($"ConnectionStrings:{x.Name}", x.Value))))
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.StubAllExternalTransports();
            });
    }

    private static AmazonSnsTransport transportFor(IHost host, string protocol = "sns")
    {
        return host.GetRuntime().Options.Transports.OfType<AmazonSnsTransport>().Single(x => x.Protocol == protocol);
    }

    [Fact]
    public async Task a_missing_name_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseAmazonSnsTransportUsingNamedConnection("aws")).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["aws"]);
        ex.Message.ShouldContain("builder.AddLocalStack(\"aws\")");
    }

    [Fact]
    public async Task a_missing_name_for_a_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
            {
                opts.UseAmazonSnsTransportUsingNamedConnection("aws");
                opts.AddNamedAmazonSnsBrokerUsingNamedConnection(new BrokerName("other"), "aws2");
            }, ("aws", "http://localhost:4566")).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["aws2"]);
        ex.Message.ShouldContain("builder.AddLocalStack(\"aws2\")");
    }

    [Theory]
    // What LocalStack.Aspire.Hosting and CommunityToolkit.Aspire.Hosting.Floci both put at ConnectionStrings:{name}
    [InlineData("http://localhost:4566")]
    // LocalStack.Aspire.Hosting with UseSsl
    [InlineData("https://localhost:4566")]
    public async Task the_named_connection_string_is_applied_to_the_default_broker(string value)
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:aws", value)]))
            .UseWolverine(opts =>
            {
                #region sample_amazon_sns_using_named_connection

                // "aws" is the name of the LocalStack resource in the Aspire AppHost,
                // i.e. builder.AddLocalStack("aws"), referenced by this project
                opts.UseAmazonSqsTransportUsingNamedConnection("aws")
                    .AutoProvision();

                opts.UseAmazonSnsTransportUsingNamedConnection("aws")
                    .AutoProvision();

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.SnsConfig.ServiceURL.ShouldStartWith(value);

        // The paired SQS config, used to provision SNS -> SQS subscriptions
        transport.SQS.Config.ServiceURL.ShouldStartWith(value);

        transport.CredentialSource.ShouldNotBeNull();
        transport.CredentialSource(host.GetRuntime()).ShouldBeOfType<BasicAWSCredentials>();
    }

    [Fact]
    public async Task sns_alone_also_points_the_paired_sqs_config_at_the_emulator()
    {
        using var host = await hostWith(opts => opts.UseAmazonSnsTransportUsingNamedConnection("aws"),
            ("aws", "http://localhost:4566")).StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).SQS.Config.ServiceURL.ShouldStartWith("http://localhost:4566");
    }

    [Fact]
    public async Task an_sqs_named_connection_keeps_its_own_service_url()
    {
        using var host = await hostWith(opts =>
            {
                // SNS registered first, so its callback runs first
                opts.UseAmazonSnsTransportUsingNamedConnection("sns");
                opts.UseAmazonSqsTransportUsingNamedConnection("sqs");
            },
            ("sns", "http://localhost:4566"), ("sqs", "http://localhost:4577"))
            .StartAsync(TestContext.Current.CancellationToken);

        var sns = transportFor(host);
        sns.SnsConfig.ServiceURL.ShouldStartWith("http://localhost:4566");
        sns.SQS.Config.ServiceURL.ShouldStartWith("http://localhost:4577");
    }

    [Fact]
    public async Task the_named_connection_string_is_applied_to_a_named_broker()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("ConnectionStrings:aws", "http://localhost:4566"),
                new KeyValuePair<string, string?>("ConnectionStrings:aws2", "http://localhost:4567")
            ]))
            .UseWolverine(opts =>
            {
                #region sample_amazon_sns_named_broker_using_named_connection

                opts.UseAmazonSnsTransportUsingNamedConnection("aws");

                // A second emulator, i.e. builder.AddLocalStack("aws2") in the AppHost
                opts.AddNamedAmazonSnsBrokerUsingNamedConnection(new BrokerName("other"), "aws2");

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).SnsConfig.ServiceURL.ShouldStartWith("http://localhost:4566");

        var other = transportFor(host, "other");
        other.SnsConfig.ServiceURL.ShouldStartWith("http://localhost:4567");
        other.SQS.Config.ServiceURL.ShouldStartWith("http://localhost:4567");
    }

    [Fact]
    public async Task the_sns_and_sqs_configuration_overload_is_applied_eagerly()
    {
        using var host = await hostWith(
            opts => opts.UseAmazonSnsTransportUsingNamedConnection("aws", (sns, sqs) =>
            {
                sns.AuthenticationRegion = "us-west-2";
                sqs.AuthenticationRegion = "us-west-1";
            }),
            ("aws", "http://localhost:4566")).StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.SnsConfig.AuthenticationRegion.ShouldBe("us-west-2");
        transport.SQS.Config.AuthenticationRegion.ShouldBe("us-west-1");
    }

    [Fact]
    public async Task a_value_that_is_not_a_service_url_fails_startup_with_a_clear_message()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseAmazonSnsTransportUsingNamedConnection("aws"),
                ("aws", "Endpoint=whatever;Key=nope")).StartAsync();
        });

        ex.Message.ShouldContain("'aws'");
        ex.Message.ShouldContain("service URL");
        ex.Message.ShouldNotContain("Key=nope");
    }

    [Fact]
    public async Task a_configured_name_connects_to_localstack_and_moves_a_message()
    {
        var name = "named-conn-" + Guid.NewGuid().ToString("N")[..12];

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:aws", "http://localhost:4566")]))
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportUsingNamedConnection("aws").AutoProvision();
                opts.ListenToSqsQueue(name).ReceiveSnsTopicMessage();

                opts.UseAmazonSnsTransportUsingNamedConnection("aws").AutoProvision();
                opts.PublishMessage<SnsMessage>().ToSnsTopic(name).SubscribeSqsQueue(name);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var message = new SnsMessage("named connection");

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .ExecuteAndWaitAsync(c => c.SendAsync(message));

        session.Received.SingleMessage<SnsMessage>().Name.ShouldBe(message.Name);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task uses_the_clients_from_the_container_and_does_not_dispose_them()
    {
        var name = "container-client-" + Guid.NewGuid().ToString("N")[..12];
        var credentials = new BasicAWSCredentials("ignore", "ignore");

        // Stand in for services.AddAWSService<IAmazonSimpleNotificationService>() / AddAWSService<IAmazonSQS>()
        var snsClient = new AmazonSimpleNotificationServiceClient(credentials,
            new AmazonSimpleNotificationServiceConfig { ServiceURL = "http://localhost:4566" });
        var sqsClient = new AmazonSQSClient(credentials, new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });

        using (var host = await Host.CreateDefaultBuilder()
                   .ConfigureServices(services =>
                   {
                       services.AddSingleton<IAmazonSimpleNotificationService>(snsClient);
                       services.AddSingleton<IAmazonSQS>(sqsClient);
                   })
                   .UseWolverine(opts =>
                   {
                       #region sample_amazon_sns_with_clients_from_container

                       // Use the IAmazonSQS and IAmazonSimpleNotificationService registered in the
                       // container, e.g. by services.AddAWSService<IAmazonSQS>()
                       opts.UseAmazonSqsTransportWithClientFromContainer().AutoProvision();
                       opts.UseAmazonSnsTransportWithClientsFromContainer().AutoProvision();

                       #endregion

                       opts.ListenToSqsQueue(name).ReceiveSnsTopicMessage();
                       opts.PublishMessage<SnsMessage>().ToSnsTopic(name).SubscribeSqsQueue(name);
                   })
                   .StartAsync(TestContext.Current.CancellationToken))
        {
            var transport = transportFor(host);
            transport.SnsClient.ShouldBeSameAs(snsClient);
            transport.SqsClient.ShouldBeSameAs(sqsClient);

            var message = new SnsMessage("container clients");
            var session = await host.TrackActivity()
                .IncludeExternalTransports()
                .Timeout(1.Minutes())
                .ExecuteAndWaitAsync(c => c.SendAsync(message));

            session.Received.SingleMessage<SnsMessage>().Name.ShouldBe(message.Name);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Still usable after the host is gone: Wolverine left the container's clients alone
        await snsClient.ListTopicsAsync(TestContext.Current.CancellationToken);
        var url = await sqsClient.GetQueueUrlAsync(name, TestContext.Current.CancellationToken);
        await sqsClient.DeleteQueueAsync(url.QueueUrl, TestContext.Current.CancellationToken);

        snsClient.Dispose();
        sqsClient.Dispose();
    }
}
