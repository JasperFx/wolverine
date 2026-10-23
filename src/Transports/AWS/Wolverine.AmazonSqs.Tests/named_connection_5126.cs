using Amazon.Runtime;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
/// GH-5126. <c>UseAmazonSqsTransportUsingNamedConnection(name)</c> /
/// <c>AddNamedAmazonSqsBrokerUsingNamedConnection(broker, name)</c> read the service URL of a local AWS emulator
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

    private static AmazonSqsTransport transportFor(IHost host, string protocol = "sqs")
    {
        return host.GetRuntime().Options.Transports.OfType<AmazonSqsTransport>().Single(x => x.Protocol == protocol);
    }

    [Fact]
    public async Task a_missing_name_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseAmazonSqsTransportUsingNamedConnection("aws")).StartAsync();
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
                opts.UseAmazonSqsTransportUsingNamedConnection("aws");
                opts.AddNamedAmazonSqsBrokerUsingNamedConnection(new BrokerName("other"), "aws2");
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
                #region sample_amazon_sqs_using_named_connection

                // "aws" is the name of the LocalStack resource in the Aspire AppHost,
                // i.e. builder.AddLocalStack("aws"), referenced by this project
                opts.UseAmazonSqsTransportUsingNamedConnection("aws")
                    .AutoProvision();

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.Config.ServiceURL.ShouldStartWith(value);

        // Placeholder credentials, as UseAmazonSqsTransportLocally() uses, since an emulator ignores them
        transport.CredentialSource.ShouldNotBeNull();
        transport.CredentialSource(host.GetRuntime()).ShouldBeOfType<BasicAWSCredentials>();
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
                #region sample_amazon_sqs_named_broker_using_named_connection

                opts.UseAmazonSqsTransportUsingNamedConnection("aws");

                // A second emulator, i.e. builder.AddLocalStack("aws2") in the AppHost
                opts.AddNamedAmazonSqsBrokerUsingNamedConnection(new BrokerName("other"), "aws2");

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).Config.ServiceURL.ShouldStartWith("http://localhost:4566");
        transportFor(host, "other").Config.ServiceURL.ShouldStartWith("http://localhost:4567");
    }

    [Fact]
    public async Task explicit_credentials_win_over_the_placeholder_credentials()
    {
        var credentials = new BasicAWSCredentials("mine", "secret");

        using var host = await hostWith(
            opts => opts.UseAmazonSqsTransportUsingNamedConnection("aws").Credentials(credentials),
            ("aws", "http://localhost:4566")).StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).CredentialSource!(host.GetRuntime()).ShouldBeSameAs(credentials);
    }

    [Fact]
    public async Task eager_configuration_is_applied_alongside_the_named_connection()
    {
        using var host = await hostWith(
            opts => opts.UseAmazonSqsTransportUsingNamedConnection("aws", config => config.AuthenticationRegion = "us-west-2"),
            ("aws", "http://localhost:4566")).StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.Config.AuthenticationRegion.ShouldBe("us-west-2");
        transport.Config.ServiceURL.ShouldStartWith("http://localhost:4566");
    }

    [Fact]
    public async Task a_value_that_is_not_a_service_url_fails_startup_with_a_clear_message()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseAmazonSqsTransportUsingNamedConnection("aws"),
                ("aws", "Endpoint=whatever;Key=nope")).StartAsync();
        });

        ex.Message.ShouldContain("'aws'");
        ex.Message.ShouldContain("service URL");
        ex.Message.ShouldNotContain("Key=nope");
    }

    [Fact]
    public async Task a_configured_name_connects_to_localstack_and_moves_a_message()
    {
        var queueName = "named-conn-" + Guid.NewGuid().ToString("N")[..12];

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:aws", "http://localhost:4566")]))
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportUsingNamedConnection("aws").AutoProvision();
                opts.ListenToSqsQueue(queueName);
                opts.PublishMessage<SqsMessage>().ToSqsQueue(queueName);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var message = new SqsMessage("named connection");

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(1.Minutes())
            .ExecuteAndWaitAsync(c => c.SendAsync(message));

        session.Received.SingleMessage<SqsMessage>().Name.ShouldBe(message.Name);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task uses_the_sqs_client_from_the_container_and_does_not_dispose_it()
    {
        var queueName = "container-client-" + Guid.NewGuid().ToString("N")[..12];

        // Stands in for services.AddAWSService<IAmazonSQS>() from AWSSDK.Extensions.NETCore.Setup
        var client = new AmazonSQSClient(new BasicAWSCredentials("ignore", "ignore"),
            new AmazonSQSConfig { ServiceURL = "http://localhost:4566" });

        using (var host = await Host.CreateDefaultBuilder()
                   .ConfigureServices(services => services.AddSingleton<IAmazonSQS>(client))
                   .UseWolverine(opts =>
                   {
                       #region sample_amazon_sqs_with_client_from_container

                       // Use the IAmazonSQS registered in the container, e.g. by
                       // services.AddAWSService<IAmazonSQS>()
                       opts.UseAmazonSqsTransportWithClientFromContainer()
                           .AutoProvision();

                       #endregion

                       opts.ListenToSqsQueue(queueName);
                       opts.PublishMessage<SqsMessage>().ToSqsQueue(queueName);
                   })
                   .StartAsync(TestContext.Current.CancellationToken))
        {
            transportFor(host).Client.ShouldBeSameAs(client);

            var message = new SqsMessage("container client");
            var session = await host.TrackActivity()
                .IncludeExternalTransports()
                .Timeout(1.Minutes())
                .ExecuteAndWaitAsync(c => c.SendAsync(message));

            session.Received.SingleMessage<SqsMessage>().Name.ShouldBe(message.Name);

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        // Still usable after the host is gone: Wolverine left the container's client alone
        var url = await client.GetQueueUrlAsync(queueName, TestContext.Current.CancellationToken);
        await client.DeleteQueueAsync(url.QueueUrl, TestContext.Current.CancellationToken);
        client.Dispose();
    }
}
