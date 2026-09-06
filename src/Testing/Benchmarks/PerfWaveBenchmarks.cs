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
    // MEASURED, and the answer has a crossover worth knowing (net9.0, --job short):
    //
    //   depth      List (before)     Queue (after)    ratio
    //     100          586 ns            677 ns       1.15x SLOWER
    //   1,000        5,922 ns          7,315 ns       1.24x SLOWER
    //  10,000      103,131 ns         69,552 ns       0.67x faster
    //  50,000    1,954,797 ns        400,482 ns       0.20x faster (4.9x)
    //
    // The quadratic claim holds: the List goes superlinear (5x more depth costs 19x more time
    // between 10k and 50k) while the Queue stays linear (5.8x). But below ~5k the memmove is
    // cheaper than Queue.Dequeue's per-item overhead, so the Queue is marginally SLOWER in the
    // common shallow case -- by ~90ns at depth 100, which is noise next to the database round
    // trip the flush is about to make. Allocation is 18% lower at every depth.
    //
    // Net: the right trade, because it bounds the pathological case (a node far behind, which is
    // exactly when completions must not get slower) at a negligible cost when things are healthy.
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

    [BenchmarkCategory("GH-4332 Drain"), Benchmark(Description = "Drain 1000 via Queue.Dequeue (after)")]
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
}
