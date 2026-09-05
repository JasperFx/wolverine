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
}
