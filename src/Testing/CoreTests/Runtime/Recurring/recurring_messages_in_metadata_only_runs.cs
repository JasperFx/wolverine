using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// The codegen and diagnostics commands (`codegen`, `wolverine-diagnostics codegen-preview`,
/// `describe-routing`, `describe`) start the host in a metadata-only mode that Wolverine forces to
/// MediatorOnly. The application never asked for that mode, so the refusal of registered schedules
/// in MediatorOnly must not stop those commands, and no recurring agent may start either: a
/// diagnostics run is not a node that should publish occurrences.
/// </summary>
public class recurring_messages_in_metadata_only_runs
{
    [Fact]
    public async Task a_codegen_command_starts_a_host_with_registered_schedules()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;

        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Schedules.ScheduleRecurring<PendingRecurringMessage>("0 9 * * *");
                }).StartAsync(TestContext.Current.CancellationToken);

            var runtime = (WolverineRuntime)host.GetRuntime();
            runtime.Options.Durability.Mode.ShouldBe(DurabilityMode.MediatorOnly);
            runtime.InMemoryRecurringAgent.ShouldBeNull();
            runtime.InMemoryRecurringTimers.ShouldBeNull();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task the_describe_command_starts_a_host_with_registered_schedules()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Schedules.ScheduleRecurring<PendingRecurringMessage>("0 9 * * *");
            }).Build();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();

        // What WolverineSystemPart does for `describe`
        await runtime.StartLightweightAsync();

        runtime.Options.Durability.Mode.ShouldBe(DurabilityMode.MediatorOnly);
        runtime.InMemoryRecurringAgent.ShouldBeNull();
        runtime.InMemoryRecurringTimers.ShouldBeNull();
    }
}
