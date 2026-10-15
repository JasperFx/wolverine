using CoreTests.Runtime.WorkerQueues;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Wolverine.Transports.SharedMemory;
using Xunit;

namespace CoreTests.Runtime;

public record ServerlessOutboxMessage(string Name);

public class ServerlessOutboxTick;

// GH-5016: DurabilityMode.ServerlessWithOutbox is the lean Serverless runtime with the outbox kept
public class serverless_with_outbox_mode_5016
{
    [Fact]
    public void outbox_stale_time_defaults_to_60_seconds_only_under_serverless_with_outbox()
    {
        new DurabilitySettings { Mode = DurabilityMode.ServerlessWithOutbox }
            .OutboxStaleTime.ShouldBe(60.Seconds());

        new DurabilitySettings { Mode = DurabilityMode.Serverless }.OutboxStaleTime.ShouldBeNull();
        new DurabilitySettings { Mode = DurabilityMode.Balanced }.OutboxStaleTime.ShouldBeNull();
        new DurabilitySettings { Mode = DurabilityMode.Solo }.OutboxStaleTime.ShouldBeNull();
    }

    [Fact]
    public void an_explicit_outbox_stale_time_always_wins()
    {
        var settings = new DurabilitySettings { Mode = DurabilityMode.ServerlessWithOutbox };
        settings.OutboxStaleTime = 5.Minutes();

        settings.OutboxStaleTime.ShouldBe(5.Minutes());

        // The order the two are set in makes no difference
        var other = new DurabilitySettings { OutboxStaleTime = 10.Seconds() };
        other.Mode = DurabilityMode.ServerlessWithOutbox;
        other.OutboxStaleTime.ShouldBe(10.Seconds());
    }

    [Theory]
    [InlineData(DurabilityMode.Balanced)]
    [InlineData(DurabilityMode.Solo)]
    [InlineData(DurabilityMode.Serverless)]
    public void outgoing_envelopes_are_owned_by_this_node_in_other_modes(DurabilityMode mode)
    {
        var settings = new DurabilitySettings { Mode = mode };
        settings.OutgoingOwnerId.ShouldBe(settings.AssignedNodeNumber);
    }

    [Fact]
    public void outgoing_envelopes_are_owned_by_any_node_under_serverless_with_outbox()
    {
        var settings = new DurabilitySettings { Mode = DurabilityMode.ServerlessWithOutbox };
        settings.OutgoingOwnerId.ShouldBe(TransportConstants.AnyNode);
    }

    [Fact]
    public async Task runs_the_lean_runtime_but_keeps_sending_endpoints_durable()
    {
        var topicName = $"serverless-outbox-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic(topicName);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();

        // Lean: no local queues, no node agent controller, no durability or scheduled-job agents
        runtime.Options.Transports.OfType<LocalTransport>().ShouldBeEmpty();
        runtime.NodeController.ShouldBeNull();
        runtime.DurableScheduledJobs.ShouldBeNull();

        // Outbox kept: the sending endpoint is durable rather than coerced to Inline
        var destination = new Uri($"shared-memory://{topicName}");
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(destination);
        agent.Endpoint.Mode.ShouldBe(EndpointMode.Durable);
        agent.IsDurable.ShouldBeTrue();

        runtime.Options.Durability.OutboxStaleTime.ShouldBe(60.Seconds());
    }

    [Fact]
    public async Task outgoing_envelopes_are_written_with_owner_id_zero()
    {
        var topicName = $"serverless-outbox-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic(topicName);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();
        var agent = runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{topicName}"));

        var route = new MessageRoute(typeof(ServerlessOutboxMessage), agent.Endpoint, runtime);
        var envelope = route.CreateForSending(new ServerlessOutboxMessage("one"), null, agent, runtime, null);

        envelope.OwnerId.ShouldBe(TransportConstants.AnyNode);
        runtime.Options.Durability.AssignedNodeNumber.ShouldNotBe(TransportConstants.AnyNode);
    }

    [Fact]
    public async Task recurring_messages_are_refused_like_plain_serverless()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Schedules.ScheduleRecurring<ServerlessOutboxTick>("0 9 * * *");
                })
                .StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain(nameof(DurabilityMode.ServerlessWithOutbox));
    }

    [Fact]
    public async Task plain_serverless_downgrading_durable_endpoints_logs_one_aggregated_warning()
    {
        var logs = new CapturedLogs();
        var first = $"serverless-durable-a-{Guid.NewGuid():N}";
        var second = $"serverless-durable-b-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Serverless;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic(first);
                opts.PublishAllMessages().ToSharedMemoryTopic(second);
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>();

        // Still downgraded, and the app still starts -- a warning, not an error
        runtime.Endpoints.GetOrBuildSendingAgent(new Uri($"shared-memory://{first}")).Endpoint.Mode
            .ShouldBe(EndpointMode.Inline);

        var warnings = logs.Warnings.Where(x => x.Contains(nameof(DurabilityMode.ServerlessWithOutbox))).ToArray();
        warnings.Length.ShouldBe(1);
        warnings[0].ShouldContain(first);
        warnings[0].ShouldContain(second);
    }

    [Fact]
    public async Task plain_serverless_without_durable_endpoints_logs_no_downgrade_warning()
    {
        var logs = new CapturedLogs();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Serverless;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic($"serverless-inline-{Guid.NewGuid():N}");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        logs.Warnings.ShouldNotContain(x => x.Contains(nameof(DurabilityMode.ServerlessWithOutbox)));
    }

    [Fact]
    public async Task serverless_with_outbox_does_not_log_the_downgrade_warning()
    {
        var logs = new CapturedLogs();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToSharedMemoryTopic($"serverless-outbox-{Guid.NewGuid():N}");
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        logs.Warnings.ShouldNotContain(x => x.Contains("has downgraded these durable endpoints"));
    }
}
