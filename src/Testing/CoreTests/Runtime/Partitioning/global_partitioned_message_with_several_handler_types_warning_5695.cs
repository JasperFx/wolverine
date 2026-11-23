using CoreTests.Runtime.WorkerQueues;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Partitioning;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5695. A global partitioned topology whose message type has more than one
/// handler type starts fine, but its slots relay the message to each handler type's own local queue and move on, so the
/// messages of one group run concurrently and out of order. Refusing it would break working applications (GH-2303), so
/// it is a startup warning until the opt-in that runs every handler type on the slot arrives
/// </summary>
public class global_partitioned_message_with_several_handler_types_warning_5695
{
    private static async Task<RecordingLoggerProvider> startHost(Action<WolverineOptions> configure, params Type[] handlerTypes)
    {
        var logs = new RecordingLoggerProvider();

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(x => x.AddProvider(logs))
            .UseWolverine(opts =>
            {
                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handlerType in handlerTypes) discovery.IncludeType(handlerType);

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
                opts.MessagePartitioning.ByMessage<WarnedGlobalCommand>(x => x.GroupId);

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);

        return logs;
    }

    private static void globalTopology(WolverineOptions opts, string baseName,
        Action<GlobalPartitionedMessageTopology>? configure = null)
    {
        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            topology.SetExternalTopology(new LocalPartitionedMessageTopology(opts, baseName, 3), baseName);
            topology.Message<WarnedGlobalCommand>();
            configure?.Invoke(topology);
        });
    }

    private static RecordingLoggerProvider.LogRecord[] orderingWarnings(RecordingLoggerProvider logs)
    {
        return logs.Records
            .Where(x => x.Level == LogLevel.Warning && x.Message.Contains("Per-group ordering is lost"))
            .ToArray();
    }

    [Fact]
    public async Task a_global_topology_with_two_handler_types_for_one_message_type_is_warned_about()
    {
        var logs = await startHost(opts => globalTopology(opts, "warnedglobal"),
            typeof(FirstWarnedGlobalHandler), typeof(SecondWarnedGlobalHandler));

        var warning = orderingWarnings(logs).ShouldHaveSingleItem().Message;

        warning.ShouldContain(typeof(WarnedGlobalCommand).FullName!);
        warning.ShouldContain("warnedglobal");
        warning.ShouldContain(typeof(FirstWarnedGlobalHandler).FullName!);
        warning.ShouldContain(typeof(SecondWarnedGlobalHandler).FullName!);
        warning.ShouldContain("HandledBy<THandler>()");
        warning.ShouldContain("opt-in");
    }

    [Fact]
    public async Task a_global_topology_with_one_handler_type_is_not_warned_about()
    {
        var logs = await startHost(opts => globalTopology(opts, "onewarnedglobal"),
            typeof(FirstWarnedGlobalHandler));

        orderingWarnings(logs).ShouldBeEmpty();
    }

    [Fact]
    public async Task two_global_topologies_that_each_declare_one_handler_type_are_not_warned_about()
    {
        // GH-5916. Each topology runs only the one handler type it declares, right on its slots, so nothing is lost
        var logs = await startHost(opts =>
            {
                globalTopology(opts, "warnedmoduleone", t => t.HandledBy(typeof(FirstWarnedGlobalHandler)));
                globalTopology(opts, "warnedmoduletwo", t => t.HandledBy(typeof(SecondWarnedGlobalHandler)));
            },
            typeof(FirstWarnedGlobalHandler), typeof(SecondWarnedGlobalHandler));

        orderingWarnings(logs).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_global_topology_that_declares_two_of_several_handler_types_is_warned_about()
    {
        var logs = await startHost(opts =>
            {
                globalTopology(opts, "warnedmodulepair",
                    t => t.HandledBy(typeof(FirstWarnedGlobalHandler), typeof(SecondWarnedGlobalHandler)));
                globalTopology(opts, "warnedmodulethird", t => t.HandledBy(typeof(ThirdWarnedGlobalHandler)));
            },
            typeof(FirstWarnedGlobalHandler), typeof(SecondWarnedGlobalHandler), typeof(ThirdWarnedGlobalHandler));

        var warning = orderingWarnings(logs).ShouldHaveSingleItem().Message;
        warning.ShouldContain("warnedmodulepair");
        warning.ShouldNotContain(nameof(ThirdWarnedGlobalHandler));
    }
}

public record WarnedGlobalCommand(string GroupId);

public static class FirstWarnedGlobalHandler
{
    public static void Handle(WarnedGlobalCommand command)
    {
    }
}

public static class SecondWarnedGlobalHandler
{
    public static void Handle(WarnedGlobalCommand command)
    {
    }
}

public static class ThirdWarnedGlobalHandler
{
    public static void Handle(WarnedGlobalCommand command)
    {
    }
}
