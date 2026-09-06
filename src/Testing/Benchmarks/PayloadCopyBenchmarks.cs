using System.Buffers;
using BenchmarkDotNet.Attributes;
using Wolverine;
using Wolverine.Runtime.Serialization;

namespace Benchmarks;

/// <summary>
/// GH-4333. The payload-copy design item is deliberately gated on evidence: "should not be built
/// until a profiling session on a realistic workload confirms payload copies dominate." This is
/// that measurement, reduced to the specific copies the design would remove, so the decision can
/// be made on numbers instead of on the shape of the code.
///
/// <para>What each pair isolates:</para>
/// <list type="bullet">
///   <item><b>Receive</b> — every broker transport does <c>body.ToArray()</c> because
///   <see cref="Envelope.Data" /> is a <c>byte[]</c> and the client's buffer dies at the end of the
///   delivery callback. The alternative is a pooled rent + copy, which is the same copy with no GC
///   allocation. The delta between these two is the entire receive-side prize.</item>
///   <item><b>Serialize</b> — <see cref="EnvelopeSerializer.Serialize(Envelope)" /> writes through a
///   <c>MemoryStream</c> and then hands back <c>ToArray()</c>. GH-4327 sized the stream; the
///   remaining question is what the final copy costs versus writing into a pooled buffer.</item>
/// </list>
///
/// <para>
/// Payload sizes span the range that matters: 1 KB is the common small message, 12 KB is the
/// production body size the GH-3971 reporter cited, and 100 KB crosses the 85 KB large-object-heap
/// threshold — where a per-message allocation stops being a cheap gen-0 bump and starts causing
/// LOH fragmentation and gen-2 pressure. If the design pays off anywhere, it pays off there, and
/// this benchmark is what tells us whether that is a real effect or a story.
/// </para>
///
/// <para>Run: <c>dotnet run -c Release -f net9.0 --project src/Testing/Benchmarks -- --filter '*PayloadCopy*'</c></para>
/// </summary>
[MemoryDiagnoser]
public class PayloadCopyBenchmarks
{
    private byte[] _brokerBuffer = null!;
    private Envelope _envelope = null!;
    private Envelope _emptyBodied = null!;

    /// <summary>
    /// 1 KB: ordinary small message. 12 KB: the production body size cited on GH-3971.
    /// 100 KB: past the 85 KB LOH threshold, where the allocation shape changes character.
    /// </summary>
    [Params(1024, 12 * 1024, 100 * 1024)]
    public int PayloadSize;

    [GlobalSetup]
    public void Setup()
    {
        _brokerBuffer = new byte[PayloadSize];
        Random.Shared.NextBytes(_brokerBuffer);

        _envelope = new Envelope
        {
            Data = _brokerBuffer,
            MessageType = "payload-copy-benchmark",
            ContentType = "application/json",
            Destination = new Uri("rabbitmq://queue/incoming"),
            ReplyUri = new Uri("rabbitmq://queue/replies")
        };

        // Identical framing, no payload — isolates what serialization costs before any body copy
        _emptyBodied = new Envelope
        {
            Data = [],
            MessageType = _envelope.MessageType,
            ContentType = _envelope.ContentType,
            Destination = _envelope.Destination,
            ReplyUri = _envelope.ReplyUri
        };
    }

    /// <summary>
    /// Today's receive path: the transport copies the client's buffer into a fresh array because
    /// Envelope.Data is a byte[] that outlives the delivery callback.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Receive: ToArray (today)")]
    public byte[] ReceiveCopyToArray()
    {
        ReadOnlySpan<byte> body = _brokerBuffer;
        return body.ToArray();
    }

    /// <summary>
    /// The proposed receive path: same copy, into a pooled buffer that is returned at the
    /// envelope's terminal. Measures what pooling actually buys once the copy itself is unavoidable.
    /// </summary>
    [Benchmark(Description = "Receive: pooled rent + copy (proposed)")]
    public int ReceiveCopyPooled()
    {
        ReadOnlySpan<byte> body = _brokerBuffer;

        var rented = ArrayPool<byte>.Shared.Rent(body.Length);
        try
        {
            body.CopyTo(rented);
            return body.Length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Today's persisted-envelope path, post-GH-4327: sized MemoryStream, then ToArray.
    /// </summary>
    [Benchmark(Description = "Serialize: to byte[] (today)")]
    public byte[] SerializeToArray()
    {
        return EnvelopeSerializer.Serialize(_envelope);
    }

    /// <summary>
    /// The floor that a persisted envelope's framing costs once the payload copy is taken out of
    /// it: serialize an envelope carrying no body at all. The gap between this and
    /// <see cref="SerializeToArray" /> is the part of serialization that is payload copying and
    /// therefore the part an <c>IBufferWriter</c>-based path could recover.
    ///
    /// <para>
    /// Deliberately NOT a "pooled serialize" benchmark: no such path exists yet, and wrapping the
    /// current allocating one in a pool rental would measure something strictly worse than today
    /// while looking like a target. Measure what exists; infer the ceiling from the difference.
    /// </para>
    /// </summary>
    [Benchmark(Description = "Serialize: framing only, no body (floor)")]
    public byte[] SerializeFramingOnly()
    {
        return EnvelopeSerializer.Serialize(_emptyBodied);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GH-4333 AS SHIPPED — MEASURED (net9.0 --job short --inProcess):
    //
    //   size    before (Data=ToArray)      after (CopyBodyFrom+Body)   legacy reader (Data)
    //    1 KB     110 ns / 1,584 B           116 ns / 1,584 B            119 ns / 1,584 B
    //   12 KB     649 ns / 12,848 B          685 ns / 12,848 B           811 ns / 12,848 B
    //  100 KB  13,464 ns / 102,974 B         999 ns /   536 B         18,658 ns / 102,974 B
    //                Gen0/1/2 3.30 each      no Gen1/Gen2                Gen0/1/2 3.30 each
    //
    // At 100 KB: 13.5x faster, allocation down to 0.5%, and the Gen1/Gen2 collections are GONE --
    // that is the large-object heap being escaped, which is the entire point of the gate.
    //
    // Below the gate the two arms allocate byte-for-byte the same, because they ARE the same code:
    // CopyBodyFrom under Envelope.PooledBodyThreshold is body.ToArray(). The small time difference
    // there is the after-arm also calling Reset(), which the before-arm does not.
    //
    // The third column is the honest cost of the escape hatch. A caller that reads Data on a pooled
    // envelope pays rent + copy + materialize instead of one copy -- 38% slower at 100 KB. That is why
    // every first-party read path (EnvelopeSerializer, System.Text.Json, Newtonsoft, MessagePack,
    // MemoryPack) was moved onto Body: a custom IMessageSerializer taking byte[] is the only shape
    // that still lands in that column, and only above 85 KB.
    // ───────────────────────────────────────────────────────────────────────── The two above are the isolated primitives; these are the real receive path
    // through Envelope, which is what actually runs. The gate at Envelope.PooledBodyThreshold (85,000
    // bytes, the LOH boundary) is the point: at 1 KB and 12 KB these two arms must be the same code,
    // and at 100 KB the pooled arm must stop allocating.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pre-GH-4333 receive path: assign a fresh array to Envelope.Data.
    /// </summary>
    [Benchmark(Description = "Envelope receive: Data = ToArray (before)")]
    public int EnvelopeReceiveBefore()
    {
        var envelope = new Envelope { Data = _brokerBuffer.AsSpan().ToArray() };
        return envelope.Data!.Length;
    }

    /// <summary>
    /// The shipped receive path: CopyBodyFrom, then read the payload through Body the way the durable
    /// insert and the JSON deserializer now do. Reset returns the rental, which is what the runtime
    /// does when the envelope is finished.
    /// </summary>
    [Benchmark(Description = "Envelope receive: CopyBodyFrom + Body (after)")]
    public int EnvelopeReceiveAfter()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(_brokerBuffer);
        var length = envelope.Body.Length;
        envelope.Reset();
        return length;
    }

    /// <summary>
    /// The escape hatch's cost. A caller that reads Data on a pooled envelope pays the materializing
    /// copy -- which is exactly what it paid before GH-4333, so legacy code cannot regress, only fail
    /// to improve. Worth measuring so that claim is a number rather than an assurance.
    /// </summary>
    [Benchmark(Description = "Envelope receive: CopyBodyFrom + Data (legacy reader)")]
    public int EnvelopeReceiveMaterialized()
    {
        var envelope = new Envelope();
        envelope.CopyBodyFrom(_brokerBuffer);
        var length = envelope.Data!.Length;
        envelope.Reset();
        return length;
    }
}
