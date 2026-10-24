using System.Reflection;
using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class ResponseQueueIdentityTests5202
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "orders")]
    [InlineData(true, "orders")]
    public async Task ResourceSetupAndRuntimeUseTheSameResponseQueue(bool autoProvision, string? prefix)
    {
        var options = new WolverineOptions { ServiceName = "ResponseQueueRepro" };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.Durability.AssignedNodeNumber = -339219470;
        var configuration = options.UseAzureServiceBus("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y")
            .EnableWolverineControlQueues();
        if (autoProvision) configuration.AutoProvision();
        if (prefix != null) configuration.SystemQueuePrefix(prefix);

        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        var runtime = runtimeFor(options);
        var created = new HashSet<string>();
        var administration = Substitute.For<ServiceBusAdministrationClient>();
        administration.QueueExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Response.FromValue(created.Contains(call.ArgAt<string>(0)),
                Substitute.For<Response>())));
        administration.CreateQueueAsync(Arg.Any<CreateQueueOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                created.Add(call.ArgAt<CreateQueueOptions>(0).Name);
                return Task.FromResult(Response.FromValue<QueueProperties>(null!, Substitute.For<Response>()));
            });
        typeof(AzureServiceBusTransport)
            .GetField("_managementClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(transport, new Lazy<ServiceBusAdministrationClient>(() => administration));

        await transport.InitializeEndpointsAsync(runtime);
        await new BrokerResource(transport, runtime).Setup(CancellationToken.None);
        var response = responseQueue(transport);
        var control = options.Transports.NodeControlEndpoint;
        var provisionedCount = created.Count;
        response.QueueName.ShouldBe((prefix == null ? "" : prefix + ".") +
            "wolverine.response.ResponseQueueRepro.-339219470");
        created.ShouldContain(response.QueueName);

        options.Durability.AssignedNodeNumber = 320;
        await transport.InitializeAsync(runtime);
        await transport.InitializeEndpointsAsync(runtime);

        responseQueue(transport).ShouldBeSameAs(response);
        options.Transports.NodeControlEndpoint.ShouldBeSameAs(control);
        (await response.CheckAsync()).ShouldBeTrue();
        created.Count.ShouldBe(provisionedCount);
        transport.Queues.ShouldNotContain(x => x.QueueName.EndsWith(".320"));
    }

    [Fact]
    public async Task FirstDiscoveryAfterRegistrationUsesTheAssignedNumber()
    {
        var options = new WolverineOptions { ServiceName = "ResponseQueueRepro" };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.UseAzureServiceBus("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y");
        options.Durability.AssignedNodeNumber = 320;
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();

        await transport.InitializeEndpointsAsync(runtimeFor(options));

        responseQueue(transport).QueueName.ShouldBe("wolverine.response.ResponseQueueRepro.320");
    }

    [Fact]
    public async Task DisabledSystemQueuesDoNotCaptureAnIdentity()
    {
        var options = new WolverineOptions { ServiceName = "ResponseQueueRepro" };
        options.Durability.Mode = DurabilityMode.Balanced;
        options.UseAzureServiceBus("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y");
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
        transport.SystemQueuesEnabled = false;
        var runtime = runtimeFor(options);
        await transport.InitializeEndpointsAsync(runtime);
        transport.Queues.ShouldBeEmpty();

        options.Durability.AssignedNodeNumber = 320;
        transport.SystemQueuesEnabled = true;
        await transport.InitializeEndpointsAsync(runtime);

        responseQueue(transport).QueueName.ShouldBe("wolverine.response.ResponseQueueRepro.320");
    }

    [Theory]
    [InlineData(DurabilityMode.Balanced)]
    [InlineData(DurabilityMode.Solo)]
    public async Task SeparateTransportsKeepSeparateResponseIdentities(DurabilityMode mode)
    {
        var first = new WolverineOptions { ServiceName = "ResponseQueueRepro" };
        var second = new WolverineOptions { ServiceName = "ResponseQueueRepro" };
        foreach (var options in new[] { first, second })
        {
            options.Durability.Mode = mode;
            options.UseAzureServiceBus("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y");
        }

        first.Durability.AssignedNodeNumber = -101;
        second.Durability.AssignedNodeNumber = -102;
        var firstTransport = first.Transports.GetOrCreate<AzureServiceBusTransport>();
        var secondTransport = second.Transports.GetOrCreate<AzureServiceBusTransport>();
        await firstTransport.InitializeEndpointsAsync(runtimeFor(first));
        await secondTransport.InitializeEndpointsAsync(runtimeFor(second));
        var firstResponse = responseQueue(firstTransport);
        var secondResponse = responseQueue(secondTransport);
        firstResponse.QueueName.ShouldNotBe(secondResponse.QueueName);

        first.Durability.AssignedNodeNumber = 1;
        second.Durability.AssignedNodeNumber = 2;
        await firstTransport.InitializeEndpointsAsync(runtimeFor(first));
        await secondTransport.InitializeEndpointsAsync(runtimeFor(second));

        responseQueue(firstTransport).ShouldBeSameAs(firstResponse);
        responseQueue(secondTransport).ShouldBeSameAs(secondResponse);
        if (mode == DurabilityMode.Solo)
        {
            firstResponse.QueueName.ShouldBe($"wolverine.response.ResponseQueueRepro.{first.UniqueNodeId:N}");
            secondResponse.QueueName.ShouldBe($"wolverine.response.ResponseQueueRepro.{second.UniqueNodeId:N}");
        }
    }

    private static IWolverineRuntime runtimeFor(WolverineOptions options)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Logger.Returns(NullLogger.Instance);
        return runtime;
    }

    private static AzureServiceBusQueue responseQueue(AzureServiceBusTransport transport) =>
        transport.Queues.Single(x => x.EndpointName == AzureServiceBusTransport.ResponseEndpointName);
}
