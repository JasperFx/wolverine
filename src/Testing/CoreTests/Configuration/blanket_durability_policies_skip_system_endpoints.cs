using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-3878: the three blanket durability policies are a statement about the *application's* messages.
/// Endpoints that Wolverine or one of its extensions configures for itself (<see cref="EndpointRole.System"/>)
/// are infrastructure the application did not author, and are frequently high volume and deliberately cheap
/// and droppable, so an app-wide "make everything durable" must not reach into them and silently multiply
/// their cost.
///
/// <para>The role check lives in the <c>AllListeners</c> / <c>AllSenders</c> / <c>AllLocalQueues</c>
/// primitives that all three policies are built on, rather than in the policies themselves, so these tests
/// pin the behaviour at the level a user actually observes.</para>
/// </summary>
public class blanket_durability_policies_skip_system_endpoints
{
    private static IEndpointPolicy[] policiesFor(Action<WolverineOptions> configure)
    {
        var options = new WolverineOptions();
        configure(options);
        return options.Transports.EndpointPolicies.ToArray();
    }

    private static void apply(IEnumerable<IEndpointPolicy> policies, Endpoint endpoint)
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        foreach (var policy in policies)
        {
            policy.Apply(endpoint, runtime);
        }

        foreach (var configuration in endpoint.DelayedConfiguration.ToArray())
        {
            configuration.Apply();
        }
    }

    [Theory]
    [InlineData(EndpointRole.System, EndpointMode.BufferedInMemory)]
    [InlineData(EndpointRole.Application, EndpointMode.Durable)]
    public void use_durable_inbox_on_all_listeners(EndpointRole role, EndpointMode expected)
    {
        var policies = policiesFor(opts => opts.Policies.UseDurableInboxOnAllListeners());

        var endpoint = new TestEndpoint(role) { IsListener = true };
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);

        apply(policies, endpoint);

        endpoint.Mode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(EndpointRole.System, EndpointMode.BufferedInMemory)]
    [InlineData(EndpointRole.Application, EndpointMode.Durable)]
    public void use_durable_outbox_on_all_sending_endpoints(EndpointRole role, EndpointMode expected)
    {
        var policies = policiesFor(opts => opts.Policies.UseDurableOutboxOnAllSendingEndpoints());

        var endpoint = new TestEndpoint(role);
        endpoint.Subscriptions.Add(Wolverine.Runtime.Routing.Subscription.All());
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);

        apply(policies, endpoint);

        endpoint.Mode.ShouldBe(expected);
    }

    /// <summary>
    /// The end-to-end shape of the GH-3878 report, exercised through the real <c>Endpoint.Compile()</c>
    /// path rather than by invoking the policies directly. The local agent queue is a genuine
    /// <see cref="EndpointRole.System"/> endpoint that Wolverine sets up for itself and deliberately
    /// leaves buffered.
    /// </summary>
    [Fact]
    public async Task use_durable_local_queues_leaves_wolverines_own_queues_alone()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.LocalQueue("app-queue");

                opts.Policies.UseDurableLocalQueues();
            }).StartAsync(TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<IWolverineRuntime>().Options;
        var queues = options.Transports.GetOrCreate<LocalTransport>();

        // The application's own queue is what the policy is actually for
        var appQueue = queues.AllQueues().Single(x => x.EndpointName == "app-queue");
        appQueue.Role.ShouldBe(EndpointRole.Application);
        appQueue.Mode.ShouldBe(EndpointMode.Durable);

        // ...while Wolverine's own agent queue keeps the cheap, droppable mode it was built with
        var agentQueue = queues.AllQueues().Single(x => x.EndpointName == TransportConstants.Agents);
        agentQueue.Role.ShouldBe(EndpointRole.System);
        agentQueue.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
