using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using ImTools;
using JasperFx.Core;
using Wolverine;

namespace Benchmarks;

/// <summary>
/// GH-4316 wave. Each pair here is "the shape the code had" versus "the shape it has now", written
/// out side by side so the comparison is measured rather than asserted. The wave shipped on
/// reasoning plus integration-lane verification; this is the part that was missing.
///
/// <para>
/// Scope and honesty: these are micro-benchmarks of isolated call shapes. They say what a
/// mechanism costs per invocation, not what a running Wolverine node gains — a 20ns saving on a
/// path that runs once per message is real but small next to a broker round trip. Anything
/// claiming end-to-end throughput has to come from the rig in src/Testing/KafkaPerfRig, not here.
/// </para>
///
/// <para>Run: <c>dotnet run -c Release -f net9.0 --project src/Testing/Benchmarks -- --filter '*PerfWave*'</c></para>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PerfWaveBenchmarks
{
    private Envelope _noHeaders = null!;
    private Envelope _withHeaders = null!;
    private string[] _acceptedDefault = null!;
    private string[] _sharedDefault = null!;
    private CancellationToken _uncancellable;
    private CancellationTokenSource _callerCts = null!;

    // GH-4324 — accumulator lookup
    private ImmutableArray<(string MessageType, Uri Destination)> _scanEntries;
    private ImHashMap<string, ImHashMap<Uri, object>> _trie = ImHashMap<string, ImHashMap<Uri, object>>.Empty;
    private string _lookupType = null!;
    private Uri _lookupUri = null!;

    // GH-4332 — coalescer drain. Depth is parameterised because the whole justification for the
    // change was "List.RemoveRange memmoves the remainder, so cost is quadratic in backlog depth".
    // A claim about depth has to be tested across depths.
    [Params(100, 1_000, 10_000, 50_000)]
    public int BacklogDepth;

    private const int FlushBatch = 100;

    // GH-4325 — rule loop
    private IList<object> _rulesAsInterface = null!;
    private object[] _rulesAsArray = null!;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [GlobalSetup]
    public void Setup()
    {
        _noHeaders = new Envelope { MessageType = "M", Destination = new Uri("rabbitmq://queue/in") };

        _withHeaders = new Envelope { MessageType = "M", Destination = new Uri("rabbitmq://queue/in") };
        _withHeaders.Headers["tenant"] = "acme";
        _withHeaders.Headers["trace"] = "abc123";

        // The shared default array, reached through the public property rather than the
        // internal field: a fresh Envelope carries exactly that instance, which is what makes
        // the reference check below viable in the real mapper.
        _acceptedDefault = new Envelope().AcceptedContentTypes!;
        _sharedDefault = _acceptedDefault;

        _uncancellable = CancellationToken.None;
        _callerCts = new CancellationTokenSource();

        // A realistic accumulator population: 12 message types x 4 destinations = 48 entries,
        // and we look up the LAST one so the scan pays its worst realistic case.
        var entries = ImmutableArray.CreateBuilder<(string, Uri)>();
        for (var t = 0; t < 12; t++)
        {
            for (var d = 0; d < 4; d++)
            {
                var type = $"MyApp.Messages.MessageType{t}";
                var uri = new Uri($"rabbitmq://queue/destination-{d}");
                entries.Add((type, uri));

                var byUri = _trie.GetValueOrDefault(type) ?? ImHashMap<Uri, object>.Empty;
                _trie = _trie.AddOrUpdate(type, byUri.AddOrUpdate(uri, new object()));
            }
        }

        _scanEntries = entries.ToImmutable();
        _lookupType = "MyApp.Messages.MessageType11";
        _lookupUri = new Uri("rabbitmq://queue/destination-3");

        _rulesAsInterface = new List<object>();   // the common case: NO rules configured
        _rulesAsArray = [];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4322 — Executor cancellation setup, per message
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4322 CTS"), Benchmark(Baseline = true, Description = "CTS: timeout + linked (before)")]
    public bool CtsBefore()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _callerCts.Token);
        return combined.Token.CanBeCanceled;
    }

    [BenchmarkCategory("GH-4322 CTS"), Benchmark(Description = "CTS: one linked + CancelAfter (after)")]
    public bool CtsAfter()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_callerCts.Token);
        cts.CancelAfter(Timeout);
        return cts.Token.CanBeCanceled;
    }

    [BenchmarkCategory("GH-4322 CTS"), Benchmark(Description = "CTS: uncancellable caller, no link (after, best case)")]
    public bool CtsAfterUncancellable()
    {
        using var cts = _uncancellable.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_uncancellable)
            : new CancellationTokenSource();
        cts.CancelAfter(Timeout);
        return cts.Token.CanBeCanceled;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4323 — the lazy Headers dictionary, per outgoing message
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4323 Headers"), Benchmark(Baseline = true, Description = "Headers: .Count forces the dictionary (before)")]
    public bool HeadersBefore()
    {
        var envelope = new Envelope { MessageType = "M" };
        return envelope.Headers.Count == 0;
    }

    [BenchmarkCategory("GH-4323 Headers"), Benchmark(Description = "Headers: HasHeaders, no allocation (after)")]
    public bool HeadersAfter()
    {
        var envelope = new Envelope { MessageType = "M" };
        return !envelope.HasHeaders;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4323 — accepted-content-types round trip, per message each way
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4323 ContentTypes"), Benchmark(Baseline = true, Description = "ContentTypes: Join then Split (before)")]
    public string[] ContentTypesBefore()
    {
        var written = _acceptedDefault.Join(",");
        return written.Split(',');
    }

    [BenchmarkCategory("GH-4323 ContentTypes"), Benchmark(Description = "ContentTypes: reference check + shared array (after)")]
    public string[] ContentTypesAfter()
    {
        var written = ReferenceEquals(_acceptedDefault, _sharedDefault)
            ? "application/json"
            : _acceptedDefault.Join(",");

        return written == "application/json" ? _acceptedDefault : written.Split(',');
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4324 — accumulator lookup, 2-4x per message on CritterWatch metric modes
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4324 Lookup"), Benchmark(Baseline = true, Description = "Accumulator: linear scan + Uri.Equals (before)")]
    public bool LookupBefore()
    {
        for (var i = 0; i < _scanEntries.Length; i++)
        {
            var entry = _scanEntries[i];
            if (entry.MessageType == _lookupType && entry.Destination == _lookupUri)
            {
                return true;
            }
        }

        return false;
    }

    [BenchmarkCategory("GH-4324 Lookup"), Benchmark(Description = "Accumulator: nested ImHashMap (after)")]
    public bool LookupAfter()
    {
        return _trie.TryFind(_lookupType, out var byUri) && byUri.TryFind(_lookupUri, out _);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4326 — clock read on the receive path, per message
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4326 Clock"), Benchmark(Baseline = true, Description = "Clock: DateTimeOffset.Now (before)")]
    public DateTimeOffset ClockBefore() => DateTimeOffset.Now;

    [BenchmarkCategory("GH-4326 Clock"), Benchmark(Description = "Clock: DateTimeOffset.UtcNow (after)")]
    public DateTimeOffset ClockAfter() => DateTimeOffset.UtcNow;

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4325 — envelope-rule loop, per outgoing message (empty is the common case)
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4325 Rules"), Benchmark(Baseline = true, Description = "Rules: foreach over IList (before)")]
    public int RulesBefore()
    {
        var n = 0;
        foreach (var rule in _rulesAsInterface)
        {
            if (rule != null) n++;
        }

        return n;
    }

    [BenchmarkCategory("GH-4325 Rules"), Benchmark(Description = "Rules: indexed for (after)")]
    public int RulesAfter()
    {
        var n = 0;
        for (var i = 0; i < _rulesAsArray.Length; i++)
        {
            if (_rulesAsArray[i] != null) n++;
        }

        return n;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4332 — InboxCompletionCoalescer drain under backlog.
    //
    // MEASURED across depths, three arms, net9.0 --job short --inProcess:
    //
    //   depth   List GetRange (orig)   Queue (GH-4332)      List CopyTo (shipping)
    //     100     540.8 ns / 4.82 KB    692.1 ns  1.28x      483.6 ns  0.89x / 0.83x alloc
    //   1,000   5,932.1 ns / 47.7 KB  7,674.1 ns  1.29x    5,538.5 ns  0.93x / 0.82x alloc
    //  10,000    94,612 ns             74,014 ns  0.78x       88,920 ns  0.94x / 0.82x alloc
    //  50,000 1,812,535 ns            370,377 ns  0.20x    1,780,025 ns  0.98x / 0.82x alloc
    //
    // GH-4332's quadratic claim holds: between 10k and 50k the List costs 19x more time for 5x
    // more depth while the Queue stays linear (5.8x), and at 50k the Queue is 4.9x faster. But the
    // crossover sits near 5,000 pending completions, and 100-1,000 is the depth a healthy node
    // actually runs at -- so in the common case the Queue is the SLOWER of the three, by ~29%.
    //
    // Hence the revert. What shipped is NOT the original "before": that drained with
    // GetRange(0, n).ToArray(), which allocates a List of n, copies n, allocates an array of n and
    // copies n again. CopyTo straight into the pre-sized array the flush already needs does it in
    // one allocation and one copy. That third arm is the best of both -- fastest of the three at
    // every depth below the crossover, and it keeps the Queue's ~18% allocation reduction at every
    // depth, which was the one thing GH-4332 won outright.
    //
    // Keep this benchmark. If a deployment is ever found sitting 10,000+ completions behind, the
    // table says exactly what to switch to and what it buys.
    // ─────────────────────────────────────────────────────────────────────────

    [BenchmarkCategory("GH-4332 Drain"), Benchmark(Baseline = true, Description = "Drain 1000 via List GetRange+RemoveRange (before)")]
    public int DrainBefore()
    {
        var pending = new List<object>(BacklogDepth);
        for (var i = 0; i < BacklogDepth; i++) pending.Add(i);

        var drained = 0;
        while (pending.Count > 0)
        {
            var take = Math.Min(FlushBatch, pending.Count);
            var batch = pending.GetRange(0, take).ToArray();
            pending.RemoveRange(0, take);
            drained += batch.Length;
        }

        return drained;
    }

    [BenchmarkCategory("GH-4332 Drain"), Benchmark(Description = "Drain 1000 via Queue.Dequeue (GH-4332, reverted)")]
    public int DrainAfter()
    {
        var pending = new Queue<object>(BacklogDepth);
        for (var i = 0; i < BacklogDepth; i++) pending.Enqueue(i);

        var drained = 0;
        while (pending.Count > 0)
        {
            var take = Math.Min(FlushBatch, pending.Count);
            var batch = new object[take];
            for (var i = 0; i < take; i++) batch[i] = pending.Dequeue();
            drained += batch.Length;
        }

        return drained;
    }

    // What actually shipped after the revert: the List the shallow numbers favour, minus the
    // intermediate List that GetRange().ToArray() allocated on every flush.
    [BenchmarkCategory("GH-4332 Drain"), Benchmark(Description = "Drain 1000 via List CopyTo+RemoveRange (reverted-to)")]
    public int DrainReverted()
    {
        var pending = new List<object>(BacklogDepth);
        for (var i = 0; i < BacklogDepth; i++) pending.Add(i);

        var drained = 0;
        while (pending.Count > 0)
        {
            var take = Math.Min(FlushBatch, pending.Count);
            var batch = new object[take];
            pending.CopyTo(0, batch, 0, take);
            pending.RemoveRange(0, take);
            drained += batch.Length;
        }

        return drained;
    }
}
