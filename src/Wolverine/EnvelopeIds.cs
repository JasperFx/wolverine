using MassTransit;
using MassTransit.NewIdProviders;

namespace Wolverine;

/// <summary>
/// The NewId generator behind <see cref="EnvelopeIdGeneration.NewId"/> and
/// <see cref="EnvelopeIdGeneration.NewIdSqlServerOrder"/>.
/// </summary>
/// <remarks>
/// The static <c>NewId</c> builds an id from the machine's network identity, a timestamp and a sequence each
/// process counts on its own, so two processes on one machine draw the same ids at the same moment -- one
/// node's cascaded message then collides with another node's in a shared inbox, and the inbox's duplicate
/// detection drops one of them. This generator mixes the process id in as well. It is Wolverine's own instance,
/// rather than <c>NewId.SetProcessIdProvider()</c>, so the ids anything else in the process draws from
/// <c>NewId</c> (MassTransit, application code) are left exactly as they were.
/// </remarks>
internal static class EnvelopeIds
{
    private static readonly Lazy<INewIdGenerator> _generator =
        new(() => Create(new DateTimeTickProvider(), new CurrentProcessIdProvider()));

    /// <summary>
    /// The same tick and worker id providers the static <c>NewId</c> uses, plus a process id
    /// </summary>
    internal static INewIdGenerator Create(ITickProvider ticks, IProcessIdProvider? processId) =>
        new LockFreeNewIdGenerator(ticks, new BestPossibleWorkerIdProvider(), processId);

    /// <summary>
    /// Ordered by time the way .NET and PostgreSQL compare GUIDs
    /// </summary>
    public static Guid NextSequentialGuid() => _generator.Value.NextSequentialGuid();

    /// <summary>
    /// Ordered by time the way SQL Server compares a <c>uniqueidentifier</c>
    /// </summary>
    public static Guid NextGuid() => _generator.Value.NextGuid();
}

/// <summary>
/// NewId's layout (timestamp, worker id, process id and a 16-bit sequence), produced without NewIdGenerator's
/// SpinLock. The tick and the sequence live in one 64-bit counter, advanced with a compare-and-swap: the tick as an
/// offset from the tick the generator was created at (48 bits, about 325 days) above the 16-bit sequence. A
/// generator that outlives that range hands over to MassTransit's NewIdGenerator, whose ticks are later than
/// anything drawn here.
/// </summary>
internal sealed class LockFreeNewIdGenerator : INewIdGenerator
{
    private const int SequenceBits = 16;
    private const long MaxOffset = (1L << (63 - SequenceBits)) - 1;

    private readonly ITickProvider _ticks;
    private readonly long _baseTick;
    private readonly int _c;
    private readonly int _d;
    private readonly short _gb;
    private readonly short _gc;
    private readonly NewIdGenerator _fallback;

    // (tick - _baseTick) << 16 | sequence
    private long _state;

    public LockFreeNewIdGenerator(ITickProvider ticks, IWorkerIdProvider workerIdProvider, IProcessIdProvider? processIdProvider)
    {
        _ticks = ticks;
        _baseTick = ticks.Ticks;
        _fallback = new NewIdGenerator(ticks, workerIdProvider, processIdProvider, 0);

        var workerId = workerIdProvider.GetWorkerId(0);
        _c = (workerId[0] << 24) | (workerId[1] << 16) | (workerId[2] << 8) | workerId[3];
        if (processIdProvider != null)
        {
            var processId = processIdProvider.GetProcessId();
            _d = (processId[0] << 24) | (processId[1] << 16);
        }
        else
        {
            _d = (workerId[4] << 24) | (workerId[5] << 16);
        }

        _gb = (short)_c;
        _gc = (short)(_c >> 16);
    }

    private bool tryNext(out long tick, out int sequence)
    {
        var offset = _ticks.Ticks - _baseTick;
        if (offset < 0) offset = 0;
        if (offset >= MaxOffset)
        {
            tick = 0;
            sequence = 0;
            return false;
        }

        var floor = offset << SequenceBits;
        while (true)
        {
            var current = Volatile.Read(ref _state);
            // A later tick starts the sequence again; within a tick the sequence counts up, and a full sequence
            // borrows the next tick, as NewIdGenerator does
            var next = floor > current ? floor : current + 1;
            if (Interlocked.CompareExchange(ref _state, next, current) == current)
            {
                tick = _baseTick + (next >> SequenceBits);
                sequence = (int)(next & 0xFFFF);
                return true;
            }
        }
    }

    public NewId Next()
    {
        if (!tryNext(out var tick, out var sequence)) return _fallback.Next();
        return new NewId((int)(tick >> 32), (int)(tick & 0xFFFFFFFF), _c, _d | sequence);
    }

    public ArraySegment<NewId> Next(NewId[] ids, int index, int count)
    {
        if (index + count > ids.Length) throw new ArgumentOutOfRangeException(nameof(count));
        for (var i = index; i < index + count; i++) ids[i] = Next();
        return new ArraySegment<NewId>(ids, index, count);
    }

    public Guid NextGuid()
    {
        if (!tryNext(out var tick, out var num)) return _fallback.NextGuid();
        var a = (int)(tick >> 32);
        var b = (int)(tick & 0xFFFFFFFF);
        var num2 = ((num << 8) | ((num >> 8) & 0xFF)) & 0xFFFF;
        return new Guid(_d | num2, _gb, _gc, (byte)(b >> 8), (byte)b, (byte)(a >> 24), (byte)(a >> 16),
            (byte)(a >> 8), (byte)a, (byte)(b >> 24), (byte)(b >> 16));
    }

    public Guid NextSequentialGuid()
    {
        if (!tryNext(out var tick, out var num)) return _fallback.NextSequentialGuid();
        var a = (int)(tick >> 32);
        var bb = (int)(tick & 0xFFFFFFFF);
        var b = (short)(bb >> 16);
        var c = (short)bb;
        var num2 = ((num << 8) | ((num >> 8) & 0xFF)) & 0xFFFF;
        var low = _d | num2;
        return new Guid(a, b, c, (byte)(_gc >> 8), (byte)_gc, (byte)(_gb >> 8), (byte)_gb,
            (byte)(low >> 24), (byte)(low >> 16), (byte)(low >> 8), (byte)low);
    }
}
