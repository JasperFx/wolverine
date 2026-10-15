using CoreTests.Runtime.WorkerQueues;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace CoreTests.Runtime;

// GH-5051: the durability mode wins over an endpoint's own UseDurableOutbox() / SendInline()
public class serverless_durability_mode_wins_5051
{
    private static async Task<(IHost host, WolverineRuntime runtime)> startAsync(DurabilityMode mode,
        CapturedLogs logs, Action<WolverineOptions> configure)
    {
        var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = mode;
                opts.Discovery.DisableConventionalDiscovery();
                configure(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();
        return (host, runtime);
    }

    private static string topicName() => $"mode-wins-{Guid.NewGuid():N}";

    [Fact]
    public async Task plain_serverless_downgrades_a_per_endpoint_durable_outbox_and_reports_it()
    {
        var logs = new CapturedLogs();
        var topic = topicName();

        var (host, runtime) = await startAsync(DurabilityMode.Serverless, logs,
            opts => opts.PublishAllMessages().ToSharedMemoryTopic(topic).UseDurableOutbox());
        using var _ = host;

        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{topic}"));
        agent.Endpoint.Mode.ShouldBe(EndpointMode.Inline);
        agent.IsDurable.ShouldBeFalse();

        // In the one aggregated downgrade warning
        var warnings = logs.Warnings.Where(x => x.Contains("has downgraded these durable endpoints")).ToArray();
        warnings.Length.ShouldBe(1);
        warnings[0].ShouldContain(topic);
    }

    [Fact]
    public async Task plain_serverless_overrides_a_per_endpoint_buffered_choice_with_a_warning()
    {
        var logs = new CapturedLogs();
        var topic = topicName();

        var (host, runtime) = await startAsync(DurabilityMode.Serverless, logs,
            opts => opts.PublishAllMessages().ToSharedMemoryTopic(topic).BufferedInMemory());
        using var _ = host;

        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{topic}")).Endpoint.Mode
            .ShouldBe(EndpointMode.Inline);

        logs.Warnings.ShouldContain(x => x.Contains(topic) && x.Contains("explicitly configured as BufferedInMemory"));
    }

    [Fact]
    public async Task serverless_with_outbox_overrides_a_per_endpoint_send_inline_with_a_warning()
    {
        var logs = new CapturedLogs();
        var topic = topicName();

        var (host, runtime) = await startAsync(DurabilityMode.ServerlessWithOutbox, logs,
            opts => opts.PublishAllMessages().ToSharedMemoryTopic(topic).SendInline());
        using var _ = host;

        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{topic}"));
        agent.Endpoint.Mode.ShouldBe(EndpointMode.Durable);
        agent.IsDurable.ShouldBeTrue();

        logs.Warnings.ShouldContain(x => x.Contains(topic) && x.Contains("explicitly configured as Inline")
                                         && x.Contains(nameof(DurabilityMode.ServerlessWithOutbox)));
    }

    [Fact]
    public async Task serverless_with_outbox_does_not_warn_about_an_endpoint_it_leaves_alone()
    {
        var logs = new CapturedLogs();
        var topic = topicName();

        var (host, _) = await startAsync(DurabilityMode.ServerlessWithOutbox, logs,
            opts => opts.PublishAllMessages().ToSharedMemoryTopic(topic).UseDurableOutbox());
        using var __ = host;

        logs.Warnings.ShouldNotContain(x => x.Contains("explicitly configured as"));
    }

    [Fact]
    public async Task plain_serverless_still_downgrades_an_endpoint_compiled_before_the_runtime_starts()
    {
        // Resource setup on startup compiles the broker endpoints before the runtime's StartAsync runs, and
        // Endpoint.Compile() applies the endpoint policies only once. The policy form, so this is only about when
        // the Serverless policy is registered, not about the order Compile() applies it in
        var logs = new CapturedLogs();
        var topic = topicName();

        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Serverless;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic(topic);
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            })
            .Build();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();
        var endpoint = runtime.Options.Transports.AllEndpoints().Single(x => x.Uri == new Uri($"shared-memory://{topic}"));
        endpoint.Compile(runtime);
        endpoint.Mode.ShouldBe(EndpointMode.Inline);

        await host.StartAsync(TestContext.Current.CancellationToken);

        runtime.Endpoints.GetOrBuildSendingAgent(endpoint.Uri).Endpoint.Mode.ShouldBe(EndpointMode.Inline);
        logs.Warnings.Where(x => x.Contains("has downgraded these durable endpoints")).ShouldHaveSingleItem()
            .ShouldContain(topic);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task plain_serverless_starts_with_resource_setup_on_startup()
    {
        // Resource setup builds the routing, and with it the local queues, before StartAsync removes the local
        // transport. The Serverless policy is registered ahead of that now, and must leave local queues alone
        var topic = topicName();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Serverless;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic(topic).UseDurableOutbox();
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();
        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{topic}")).Endpoint.Mode
            .ShouldBe(EndpointMode.Inline);
    }

    [Theory]
    [InlineData(DurabilityMode.Balanced)]
    [InlineData(DurabilityMode.Solo)]
    public async Task other_modes_keep_the_per_endpoint_choice(DurabilityMode mode)
    {
        var logs = new CapturedLogs();
        var inlineTopic = topicName();
        var durableTopic = topicName();

        var (host, runtime) = await startAsync(mode, logs, opts =>
        {
            opts.PublishMessage<ServerlessOutboxMessage>().ToSharedMemoryTopic(inlineTopic).SendInline();
            opts.PublishMessage<ServerlessOutboxTick>().ToSharedMemoryTopic(durableTopic).UseDurableOutbox();
        });
        using var _ = host;

        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{inlineTopic}")).Endpoint.Mode
            .ShouldBe(EndpointMode.Inline);
        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{durableTopic}")).Endpoint.Mode
            .ShouldBe(EndpointMode.Durable);

        logs.Warnings.ShouldNotContain(x => x.Contains("explicitly configured as"));
    }
}
