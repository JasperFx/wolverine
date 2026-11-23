using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Partitioning;

/// <summary>
/// https://github.com/JasperFx/wolverine/issues/5916. Two partitioned topologies that publish the same message type --
/// the "modular monolith" setup in the partitioning docs -- ran every handler of that message type once per topology.
/// Each topology now runs only the handler types it declares with HandledBy()
/// </summary>
public class two_global_topologies_for_one_message_type_5916
{
    private static Task<IHost> startHost(Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ModuleOneTwiceRoutedHandler))
                    .IncludeType(typeof(ModuleTwoTwiceRoutedHandler));

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.MessagePartitioning.ByMessage<TwiceRoutedCommand>(x => x.GroupId);

                configure(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static void globalTopology(WolverineOptions opts, string baseName,
        Action<GlobalPartitionedMessageTopology>? configure = null)
    {
        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            topology.SetExternalTopology(new LocalPartitionedMessageTopology(opts, baseName, 3), baseName);
            topology.Message<TwiceRoutedCommand>();
            configure?.Invoke(topology);
        });
    }

    private static Task publishAsync(IHost host, params int[] numbers)
    {
        return host.TrackActivity().Timeout(15.Seconds()).ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async c =>
        {
            foreach (var number in numbers)
            {
                await c.PublishAsync(new TwiceRoutedCommand("g" + (number % 2), number));
            }
        }));
    }

    [Fact]
    public async Task with_one_topology_every_handler_runs_once_there()
    {
        TwiceRoutedCounts.Reset();

        using var host = await startHost(opts => globalTopology(opts, "onlyone"));

        await publishAsync(host, 7);

        TwiceRoutedCounts.For("one", 7).ShouldBe(1);
        TwiceRoutedCounts.For("two", 7).ShouldBe(1);
    }

    [Fact]
    public async Task with_one_topology_a_declaration_does_not_narrow_its_handlers()
    {
        TwiceRoutedCounts.Reset();

        using var host = await startHost(opts =>
            globalTopology(opts, "onlyonedeclared", t => t.HandledBy<ModuleOneTwiceRoutedHandler>()));

        await publishAsync(host, 7);

        TwiceRoutedCounts.For("one", 7).ShouldBe(1);
        TwiceRoutedCounts.For("two", 7).ShouldBe(1);
    }

    [Fact]
    public async Task with_two_topologies_each_runs_only_the_handlers_it_declares()
    {
        TwiceRoutedCounts.Reset();

        using var host = await startHost(opts =>
        {
            globalTopology(opts, "moduleone", t => t.HandledBy<ModuleOneTwiceRoutedHandler>());
            globalTopology(opts, "moduletwo", t => t.HandledBy(typeof(ModuleTwoTwiceRoutedHandler)));
        });

        await publishAsync(host, 7);

        TwiceRoutedCounts.For("one", 7).ShouldBe(1);
        TwiceRoutedCounts.For("two", 7).ShouldBe(1);

        // Each handler ran on its own topology's queues. The "external" slots here are local queues, so they run
        // the message themselves rather than bridging into the global-* companion queues
        TwiceRoutedCounts.QueuesFor("one").ShouldAllBe(x => x.Contains("moduleone"));
        TwiceRoutedCounts.QueuesFor("two").ShouldAllBe(x => x.Contains("moduletwo"));
    }

    [Fact]
    public async Task with_two_topologies_and_several_messages_each_handler_runs_once_per_message()
    {
        TwiceRoutedCounts.Reset();

        using var host = await startHost(opts =>
        {
            globalTopology(opts, "moduleonemany", t => t.HandledBy<ModuleOneTwiceRoutedHandler>());
            globalTopology(opts, "moduletwomany", t => t.HandledBy<ModuleTwoTwiceRoutedHandler>());
        });

        await publishAsync(host, 1, 2, 3, 4, 5);

        for (var i = 1; i <= 5; i++)
        {
            TwiceRoutedCounts.For("one", i).ShouldBe(1);
            TwiceRoutedCounts.For("two", i).ShouldBe(1);
        }
    }

    [Fact]
    public async Task two_topologies_that_declare_no_handlers_are_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => startHost(opts =>
        {
            globalTopology(opts, "undeclaredone");
            globalTopology(opts, "undeclaredtwo");
        }));

        ex.Message.ShouldContain(nameof(TwiceRoutedCommand));
        ex.Message.ShouldContain("HandledBy");
        ex.Message.ShouldContain("undeclaredone");
        ex.Message.ShouldContain("undeclaredtwo");
    }

    [Fact]
    public async Task a_handler_that_no_topology_declares_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => startHost(opts =>
        {
            globalTopology(opts, "claimsone", t => t.HandledBy<ModuleOneTwiceRoutedHandler>());
            globalTopology(opts, "claimsnothing");
        }));

        ex.Message.ShouldContain(nameof(ModuleTwoTwiceRoutedHandler));
        ex.Message.ShouldNotContain(nameof(ModuleOneTwiceRoutedHandler));
    }

    [Fact]
    public async Task a_handler_declared_on_two_topologies_is_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => startHost(opts =>
        {
            globalTopology(opts, "bothone",
                t => t.HandledBy(typeof(ModuleOneTwiceRoutedHandler), typeof(ModuleTwoTwiceRoutedHandler)));
            globalTopology(opts, "bothtwo", t => t.HandledBy<ModuleTwoTwiceRoutedHandler>());
        }));

        ex.Message.ShouldContain(nameof(ModuleTwoTwiceRoutedHandler));
        ex.Message.ShouldContain("more than one");
    }

    [Fact]
    public async Task with_two_local_topologies_each_runs_only_the_handlers_it_declares()
    {
        TwiceRoutedCounts.Reset();

        using var host = await startHost(opts =>
        {
            opts.MessagePartitioning.PublishToPartitionedLocalMessaging("localone", 3, t =>
            {
                t.Message<TwiceRoutedCommand>();
                t.HandledBy<ModuleOneTwiceRoutedHandler>();
            });

            opts.MessagePartitioning.PublishToPartitionedLocalMessaging("localtwo", 3, t =>
            {
                t.Message<TwiceRoutedCommand>();
                t.HandledBy<ModuleTwoTwiceRoutedHandler>();
            });
        });

        await publishAsync(host, 1, 2, 3);

        for (var i = 1; i <= 3; i++)
        {
            TwiceRoutedCounts.For("one", i).ShouldBe(1);
            TwiceRoutedCounts.For("two", i).ShouldBe(1);
        }

        // Each handler runs on its own topology's slots, so it is sequenced there
        TwiceRoutedCounts.QueuesFor("one").ShouldAllBe(x => x.StartsWith("local://localone"));
        TwiceRoutedCounts.QueuesFor("two").ShouldAllBe(x => x.StartsWith("local://localtwo"));
    }

    [Fact]
    public async Task two_local_topologies_that_declare_no_handlers_are_refused_at_startup()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ModuleOneTwiceRoutedHandler));
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                    opts.MessagePartitioning.ByMessage<TwiceRoutedCommand>(x => x.GroupId);
                    opts.MessagePartitioning.PublishToPartitionedLocalMessaging("localundeclaredone", 3,
                        t => t.Message<TwiceRoutedCommand>());
                    opts.MessagePartitioning.PublishToPartitionedLocalMessaging("localundeclaredtwo", 3,
                        t => t.Message<TwiceRoutedCommand>());
                }).StartAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(nameof(TwiceRoutedCommand));
        ex.Message.ShouldContain("HandledBy");
    }
}

public record TwiceRoutedCommand(string GroupId, int Number);

public static class TwiceRoutedCounts
{
    private static readonly Dictionary<string, int> _counts = new();
    private static readonly List<(string Handler, string Queue)> _queues = new();
    private static readonly object _lock = new();

    public static void Reset()
    {
        lock (_lock)
        {
            _counts.Clear();
            _queues.Clear();
        }
    }

    public static void Record(string handler, int number, Envelope envelope)
    {
        var key = $"{handler}:{number}";
        lock (_lock)
        {
            _counts[key] = _counts.TryGetValue(key, out var count) ? count + 1 : 1;
            _queues.Add((handler, envelope.Destination?.ToString() ?? ""));
        }
    }

    public static int For(string handler, int number)
    {
        lock (_lock) return _counts.TryGetValue($"{handler}:{number}", out var count) ? count : 0;
    }

    public static string[] QueuesFor(string handler)
    {
        lock (_lock) return _queues.Where(x => x.Handler == handler).Select(x => x.Queue).ToArray();
    }
}

public class ModuleOneTwiceRoutedHandler
{
    public static void Handle(TwiceRoutedCommand command, Envelope envelope) =>
        TwiceRoutedCounts.Record("one", command.Number, envelope);
}

public class ModuleTwoTwiceRoutedHandler
{
    public static void Handle(TwiceRoutedCommand command, Envelope envelope) =>
        TwiceRoutedCounts.Record("two", command.Number, envelope);
}
