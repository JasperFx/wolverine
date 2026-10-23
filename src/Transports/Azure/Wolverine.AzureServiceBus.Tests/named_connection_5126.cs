using Azure.Core;
using Azure.Identity;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5126. <c>UseAzureServiceBusUsingNamedConnection(name)</c> /
/// <c>AddNamedAzureServiceBusBrokerUsingNamedConnection(broker, name)</c> read the connection from
/// <c>ConnectionStrings:{name}</c>, which is where a .NET Aspire AppHost's <c>builder.AddAzureServiceBus("name")</c>
/// + <c>.WithReference(...)</c> puts it. Everything but the last test runs with stubbed transports.
/// </summary>
public class named_connection_5126
{
    // What Aspire.Hosting.Azure.ServiceBus hands out for RunAsEmulator() (host port is random under Aspire)
    private const string EmulatorValue =
        "Endpoint=sb://localhost:61234;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

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

    private static AzureServiceBusTransport transportFor(IHost host, BrokerName? brokerName = null)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<AzureServiceBusTransport>(brokerName);
    }

    [Fact]
    public async Task a_missing_name_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseAzureServiceBusUsingNamedConnection("sb"))
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["sb"]);
        ex.Message.ShouldContain("builder.AddAzureServiceBus(\"sb\")");
    }

    [Fact]
    public async Task a_missing_name_for_a_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
            {
                opts.UseAzureServiceBusUsingNamedConnection("sb");
                opts.AddNamedAzureServiceBusBrokerUsingNamedConnection(new BrokerName("other"), "sb2");
            }, ("sb", EmulatorValue)).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["sb2"]);
        ex.Message.ShouldContain("builder.AddAzureServiceBus(\"sb2\")");
    }

    [Fact]
    public async Task a_connection_string_is_applied_to_the_default_broker()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:sb", EmulatorValue)]))
            .UseWolverine(opts =>
            {
                #region sample_azure_service_bus_using_named_connection

                // "sb" is the name of the Azure Service Bus resource in the Aspire AppHost,
                // i.e. builder.AddAzureServiceBus("sb"), referenced by this project. Against a
                // real namespace Aspire supplies only the namespace endpoint, and Wolverine
                // connects with a DefaultAzureCredential unless you pass a TokenCredential
                opts.UseAzureServiceBusUsingNamedConnection("sb")
                    .AutoProvision();

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.ConnectionString.ShouldBe(EmulatorValue);
        transport.FullyQualifiedNamespace.ShouldBeNull();
        transport.TokenCredential.ShouldBeNull();
    }

    [Theory]
    // What Aspire.Hosting.Azure.ServiceBus hands out for a provisioned namespace: its serviceBusEndpoint output
    [InlineData("https://myns.servicebus.windows.net:443/")]
    [InlineData("myns.servicebus.windows.net")]
    [InlineData("sb://myns.servicebus.windows.net/")]
    // Aspire's GetConnectionString(queue) form for a referenced queue on a real namespace
    [InlineData("Endpoint=https://myns.servicebus.windows.net:443/;EntityPath=orders")]
    public async Task a_namespace_is_applied_with_a_default_azure_credential(string value)
    {
        using var host = await hostWith(opts => opts.UseAzureServiceBusUsingNamedConnection("sb"), ("sb", value))
            .StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.FullyQualifiedNamespace.ShouldBe("myns.servicebus.windows.net");
        transport.TokenCredential.ShouldBeOfType<DefaultAzureCredential>();
        transport.ConnectionString.ShouldBeNull();
    }

    [Fact]
    public async Task a_namespace_uses_the_supplied_token_credential()
    {
        var credential = new FakeCredential();

        using var host = await hostWith(
                opts => opts.UseAzureServiceBusUsingNamedConnection("sb", tokenCredential: credential),
                ("sb", "https://myns.servicebus.windows.net:443/"))
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).TokenCredential.ShouldBeSameAs(credential);
    }

    [Fact]
    public async Task an_entity_path_is_dropped_from_a_full_connection_string()
    {
        using var host = await hostWith(opts => opts.UseAzureServiceBusUsingNamedConnection("sb"),
                ("sb", EmulatorValue + "EntityPath=orders"))
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).ConnectionString.ShouldBe(EmulatorValue);
    }

    [Fact]
    public async Task configure_is_applied_to_the_client_options()
    {
        using var host = await hostWith(
                opts => opts.UseAzureServiceBusUsingNamedConnection("sb",
                    o => o.Identifier = "named-5126"),
                ("sb", EmulatorValue))
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).ClientOptions.Identifier.ShouldBe("named-5126");
    }

    [Fact]
    public async Task connection_strings_are_applied_to_the_named_broker()
    {
        var other = new BrokerName("other");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("ConnectionStrings:sb", EmulatorValue),
                new KeyValuePair<string, string?>("ConnectionStrings:sb2", "https://other.servicebus.windows.net:443/")
            ]))
            .UseWolverine(opts =>
            {
                #region sample_azure_service_bus_named_broker_using_named_connection

                opts.UseAzureServiceBusUsingNamedConnection("sb");

                // A second namespace, registered in the AppHost as builder.AddAzureServiceBus("sb2")
                opts.AddNamedAzureServiceBusBrokerUsingNamedConnection(new BrokerName("other"), "sb2");

                opts.PublishAllMessages().ToAzureServiceBusQueueOnNamedBroker(new BrokerName("other"), "outgoing");

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).ConnectionString.ShouldBe(EmulatorValue);

        var named = transportFor(host, other);
        named.FullyQualifiedNamespace.ShouldBe("other.servicebus.windows.net");
        named.TokenCredential.ShouldBeOfType<DefaultAzureCredential>();
        named.ConnectionString.ShouldBeNull();
    }

    [Fact]
    public async Task a_connection_string_is_applied_to_the_named_broker()
    {
        var other = new BrokerName("other");

        using var host = await hostWith(
                opts => opts.AddNamedAzureServiceBusBrokerUsingNamedConnection(other, "sb2"),
                ("sb2", EmulatorValue))
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host, other).ConnectionString.ShouldBe(EmulatorValue);
    }

    [Fact]
    public async Task end_to_end_through_a_named_connection_against_the_emulator()
    {
        var queueName = "named-5126-" + Guid.NewGuid().ToString("N")[..8];

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:sb", Servers.AzureServiceBusConnectionString)]))
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(NamedConnection5126Handler));

                opts.UseAzureServiceBusUsingNamedConnection("sb").AutoProvision().AutoPurgeOnStartup();

                // Aspire's emulator connection string names only the AMQP port; provisioning needs the
                // emulator's management (HTTP) port
                opts.Transports.GetOrCreate<AzureServiceBusTransport>().ManagementConnectionString =
                    Servers.AzureServiceBusManagementConnectionString;

                opts.ListenToAzureServiceBusQueue(queueName);
                opts.PublishMessage<NamedConnection5126Message>().ToAzureServiceBusQueue(queueName);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var session = await host.TrackActivity()
                .IncludeExternalTransports()
                .Timeout(30.Seconds())
                .SendMessageAndWaitAsync(new NamedConnection5126Message("hello"));

            session.Received.SingleMessage<NamedConnection5126Message>().Text.ShouldBe("hello");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
        }
    }

    private class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("fake", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

public record NamedConnection5126Message(string Text);

public static class NamedConnection5126Handler
{
    public static void Handle(NamedConnection5126Message message)
    {
    }
}
