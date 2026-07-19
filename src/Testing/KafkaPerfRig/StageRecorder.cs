using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KafkaPerfRig;

public readonly record struct StageSample(
    string Kind, // "small" | "large"
    bool Warmup,
    long T0, // publish call (producer process)
    long T2, // consume return / envelope mapping (consumer process)
    long T3, // handler entry
    long T4); // handler exit

/// <summary>
/// Consumer-side capture of per-message stage timestamps. Everything is raw monotonic ticks;
/// intervals are computed at dump time. Dumps a raw CSV plus a percentile summary (stdout + JSON).
/// </summary>
public static class StageRecorder
{
    private static readonly ConcurrentQueue<StageSample> _samples = new();
    private static long _received;

    public static void Record(in StageSample sample)
    {
        _samples.Enqueue(sample);
        var count = Interlocked.Increment(ref _received);
        if (count % 500 == 0)
        {
            Console.WriteLine($"[rig] received {count} messages");
        }
    }

    public static long Received => Interlocked.Read(ref _received);

    private static double toMs(long fromTicks, long toTicks)
    {
        return (toTicks - fromTicks) * 1000.0 / Stopwatch.Frequency;
    }

    public static void Dump(string outDir, string label, object scenario)
    {
        Directory.CreateDirectory(outDir);
        var samples = _samples.ToArray();

        var csv = new StringBuilder("kind,warmup,transit_ms,dwell_ms,handler_ms,total_ms\n");
        foreach (var s in samples)
        {
            csv.Append(
                $"{s.Kind},{s.Warmup},{toMs(s.T0, s.T2):F3},{toMs(s.T2, s.T3):F3},{toMs(s.T3, s.T4):F3},{toMs(s.T0, s.T4):F3}\n");
        }

        var csvPath = Path.Combine(outDir, $"{label}-stages.csv");
        File.WriteAllText(csvPath, csv.ToString());

        var summary = new Dictionary<string, object> { ["scenario"] = scenario, ["samples"] = samples.Length };
        foreach (var kind in new[] { "small", "large" })
        {
            var measured = samples.Where(s => s.Kind == kind && !s.Warmup).ToArray();
            if (measured.Length == 0)
            {
                continue;
            }

            summary[kind] = new Dictionary<string, object>
            {
                ["count"] = measured.Length,
                ["transit_ms"] = percentiles(measured.Select(s => toMs(s.T0, s.T2))),
                ["dwell_ms"] = percentiles(measured.Select(s => toMs(s.T2, s.T3))),
                ["handler_ms"] = percentiles(measured.Select(s => toMs(s.T3, s.T4))),
                ["total_ms"] = percentiles(measured.Select(s => toMs(s.T0, s.T4)))
            };
        }

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outDir, $"{label}-summary.json"), json);
        Console.WriteLine($"[rig] {label}: wrote {samples.Length} samples to {csvPath}");
        Console.WriteLine(json);
    }

    private static Dictionary<string, double> percentiles(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        double at(double p)
        {
            var index = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }

        return new Dictionary<string, double>
        {
            ["p50"] = Math.Round(at(50), 3),
            ["p95"] = Math.Round(at(95), 3),
            ["p99"] = Math.Round(at(99), 3),
            ["max"] = Math.Round(sorted[^1], 3)
        };
    }
}
