using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Tracking;

namespace KafkaPerfRig;

/// <summary>
/// GH-5150. Broker-free, database-free in-process dispatch: N concurrent loops calling
/// <c>InvokeAsync&lt;T&gt;</c> against an async handler. Nothing here waits on I/O, so what it measures is
/// Wolverine's own dispatch path plus the generated handler, which is exactly the code runtime-async
/// changes (state machines versus runtime-managed async). Built three ways by <c>runtime-async.sh</c>:
/// net10.0, net11.0, and net11.0 with <c>-p:WolverineRuntimeAsync=true</c> plus
/// <c>RIG_RUNTIME_ASYNC=1</c> (<c>CodeGeneration.UseRuntimeAsync</c> for the generated handlers).
///
/// <para>
/// <b>Proof before numbers.</b> The Roslyn feature flag can be ignored silently (Roslyn 4.x does), so at
/// startup the lane reports, for both Wolverine.dll and the runtime-compiled handler, how many methods
/// carry <c>MethodImplAttributes.Async</c> (0x2000) and how many compiler-built async state machines
/// exist. A runtime-async build has the first and not the second. The lane refuses to run when
/// <c>RIG_EXPECT_RUNTIME_ASYNC</c> disagrees with what the IL says.
/// </para>
///
/// <para>
/// Reported per run: throughput, CPU time per message (<c>Process.TotalProcessorTime</c>, which is far
/// less sensitive than wall-clock throughput to other work on a shared box), allocated bytes per message
/// (<c>GC.GetTotalAllocatedBytes(precise: true)</c>), GC counts, and sampled invoke latency.
/// </para>
/// </summary>
public static class InProcLane
{
    private const MethodImplAttributes AsyncFlag = (MethodImplAttributes)0x2000;

    public static async Task RunAsync(RigConfig cfg)
    {
        InProcHandler.YieldEvery = cfg.InProcYieldEvery;

        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
                opts.ApplicationAssembly = typeof(InProcHandler).Assembly;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<InProcHandler>();
                opts.CodeGeneration.UseRuntimeAsync = cfg.RuntimeAsync;
            });

        using var host = builder.Build();
        await host.StartAsync();

        var bus = host.MessageBus();

        // Force the handler to be compiled now, outside the measured window, then inspect it
        await bus.InvokeAsync<InProcReply>(new InProcCommand(0));
        var proof = buildProof(host);
        Console.WriteLine($"[rig] inproc proof: {JsonSerializer.Serialize(proof)}");

        if (cfg.ExpectRuntimeAsync is { } expected)
        {
            var actual = proof.WolverineAsyncMethods > 0 && proof.HandlerAsyncMethods > 0 &&
                         proof.HandlerStateMachines == 0;
            var none = proof.WolverineAsyncMethods == 0 && proof.HandlerAsyncMethods == 0;
            if (expected ? !actual : !none)
            {
                Console.WriteLine(
                    $"[rig] REFUSING TO MEASURE: RIG_EXPECT_RUNTIME_ASYNC={(expected ? 1 : 0)} but the IL says otherwise");
                await host.StopAsync();
                Environment.Exit(3);
            }
        }

        var concurrency = cfg.InProcConcurrency > 0 ? cfg.InProcConcurrency : Environment.ProcessorCount;
        Console.WriteLine(
            $"[rig] inproc up: framework={RuntimeInformationText()} concurrency={concurrency} yieldEvery={cfg.InProcYieldEvery} " +
            $"warmup={cfg.WarmupSeconds}s duration={cfg.DurationSeconds}s");

        var phase = new Phase();
        var counts = new long[concurrency];
        var samples = new List<long>[concurrency];
        for (var i = 0; i < concurrency; i++) samples[i] = new List<long>(1 << 16);

        var loops = Enumerable.Range(0, concurrency).Select(i => Task.Run(async () =>
        {
            var seq = 0;
            var local = samples[i];
            while (!phase.Done)
            {
                seq++;
                var measuring = phase.Measuring;
                if (measuring && (seq & 63) == 0)
                {
                    var start = Stopwatch.GetTimestamp();
                    await bus.InvokeAsync<InProcReply>(new InProcCommand(seq));
                    local.Add(Stopwatch.GetTimestamp() - start);
                }
                else
                {
                    await bus.InvokeAsync<InProcReply>(new InProcCommand(seq));
                }

                if (measuring) counts[i]++;
            }
        })).ToArray();

        await Task.Delay(TimeSpan.FromSeconds(cfg.WarmupSeconds));

        // Settle the warmup garbage so the measured window starts from the same place in every variant
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu0 = process.TotalProcessorTime;
        var alloc0 = GC.GetTotalAllocatedBytes(true);
        var gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var wall = Stopwatch.StartNew();
        phase.Measuring = true;

        await Task.Delay(TimeSpan.FromSeconds(cfg.DurationSeconds));

        phase.Measuring = false;
        wall.Stop();
        var alloc1 = GC.GetTotalAllocatedBytes(true);
        process.Refresh();
        var cpu1 = process.TotalProcessorTime;
        var gc1 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        phase.Done = true;
        await Task.WhenAll(loops);
        await host.StopAsync();

        var total = counts.Sum();
        var latencies = samples.SelectMany(x => x).Select(t => t * 1_000_000.0 / Stopwatch.Frequency).Order()
            .ToArray();

        double at(double p)
        {
            if (latencies.Length == 0) return 0;
            var index = (int)Math.Ceiling(p / 100.0 * latencies.Length) - 1;
            return Math.Round(latencies[Math.Clamp(index, 0, latencies.Length - 1)], 2);
        }

        var summary = new Dictionary<string, object>
        {
            ["variant"] = cfg.Variant,
            ["framework"] = RuntimeInformationText(),
            ["runtimeAsyncCodegen"] = cfg.RuntimeAsync,
            ["proof"] = proof,
            ["concurrency"] = concurrency,
            ["yieldEvery"] = cfg.InProcYieldEvery,
            ["messages"] = total,
            ["seconds"] = Math.Round(wall.Elapsed.TotalSeconds, 2),
            ["msgPerSec"] = Math.Round(total / wall.Elapsed.TotalSeconds, 0),
            ["cpuUsPerMsg"] = Math.Round((cpu1 - cpu0).TotalMicroseconds / total, 3),
            ["allocBytesPerMsg"] = Math.Round((alloc1 - alloc0) / (double)total, 1),
            ["gen0"] = gc1.Item1 - gc0.Item1,
            ["gen1"] = gc1.Item2 - gc0.Item2,
            ["gen2"] = gc1.Item3 - gc0.Item3,
            ["latencySamples"] = latencies.Length,
            ["p50us"] = at(50),
            ["p95us"] = at(95),
            ["p99us"] = at(99)
        };

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(cfg.OutDir);
        File.WriteAllText(Path.Combine(cfg.OutDir, "inproc-summary.json"), json);
        Console.WriteLine("[rig] inproc summary:");
        Console.WriteLine(json);
    }

    private static string RuntimeInformationText() => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    private static InProcProof buildProof(IHost host)
    {
        var handler = host.GetRuntime().Handlers.HandlerFor<InProcCommand>()
                      ?? throw new InvalidOperationException("No handler for InProcCommand");

        var handlerType = handler.GetType();
        var handlerAssembly = handlerType.Assembly;

        var (wolverineAsync, wolverineMachines) = inspect(typeof(IMessageBus).Assembly.GetTypes());
        var (handlerAsync, handlerMachines) = inspect(handlerAssembly.GetTypes());

        return new InProcProof(handlerType.FullName ?? handlerType.Name, wolverineAsync, wolverineMachines,
            handlerAsync, handlerMachines);
    }

    private static (int asyncMethods, int stateMachines) inspect(IEnumerable<Type> types)
    {
        var asyncMethods = 0;
        var stateMachines = 0;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in types)
        {
            if (typeof(IAsyncStateMachine).IsAssignableFrom(type) && type.IsDefined(typeof(CompilerGeneratedAttribute)))
            {
                stateMachines++;
            }

            foreach (var method in type.GetMethods(all))
            {
                if ((method.MethodImplementationFlags & AsyncFlag) != 0) asyncMethods++;
            }
        }

        return (asyncMethods, stateMachines);
    }

    private class Phase
    {
        public volatile bool Measuring;
        public volatile bool Done;
    }
}

public record InProcProof(
    string HandlerType,
    int WolverineAsyncMethods,
    int WolverineStateMachines,
    int HandlerAsyncMethods,
    int HandlerStateMachines);

public record InProcCommand(int Seq);

public record InProcReply(int Seq);

/// <summary>
/// Genuinely async on purpose: a handler that only returns a Task is a pass-through that runtime-async
/// leaves alone. <c>YieldEvery</c> = 0 completes every await synchronously (the common shape of a
/// handler whose I/O hits a warm cache); n makes every n-th call actually suspend.
/// </summary>
public class InProcHandler
{
    public static int YieldEvery;

    public static async Task<InProcReply> Handle(InProcCommand command)
    {
        await maybeYieldAsync(command.Seq);
        return new InProcReply(command.Seq);
    }

    private static async ValueTask maybeYieldAsync(int seq)
    {
        if (YieldEvery > 0 && seq % YieldEvery == 0)
        {
            await Task.Yield();
        }
    }
}
