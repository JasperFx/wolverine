namespace Wolverine.Runtime.WorkerQueues;

/// <summary>
/// GH-3710. The default <see cref="IIncomingIdempotencyGuard"/>: a <b>generational</b> set of recently seen
/// message ids.
///
/// <para>
/// Two hash sets per category are kept, a current generation and the previous one, and membership is checked
/// across both. Rotation -- previous is dropped, current becomes previous, a fresh current is started --
/// happens on whichever comes first of half the configured window elapsing or the tracked count reaching half
/// of <see cref="InMemoryIdempotencySettings.MaxTracked"/>. That is the whole eviction policy, and it is
/// deliberately cruder than an LRU: no per-entry timestamps, no per-entry bookkeeping, O(1) on every
/// operation, and a hard ceiling on memory. The price is that an id's lifetime is a range rather than a
/// number -- remembered for at least <c>Window / 2</c> and at most <c>Window</c>, and less than that under a
/// flood of unique ids, which is exactly the workload where bounded memory matters more than a precise
/// window.
/// </para>
///
/// <para>
/// In-flight ids are tracked separately from processed ids so that a duplicate arriving <i>while</i> the
/// original is still executing is dropped rather than queued up to run again. In-flight ids are generational
/// too, purely as a leak stop: every receiver path releases what it claims, but an id that somehow never
/// reaches a terminal still ages out instead of accumulating forever.
/// </para>
///
/// <para>
/// Locking is a plain <c>lock</c> rather than <c>ImHashMap</c> swap-on-write. This is not a per-dispatch
/// dictionary lookup on the hot path -- it runs once per broker delivery, alongside deserialization and a
/// network settle -- and a mutating set with a read-modify-write on every call is the shape a copy-on-write
/// trie is worst at.
/// </para>
/// </summary>
internal class GenerationalIdempotencyGuard : IIncomingIdempotencyGuard
{
    private readonly int _generationLimit;
    private readonly MessageIdentity _identity;
    private readonly object _locker = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _rotation;

    private HashSet<IdempotencyKey> _currentInFlight = new();
    private HashSet<IdempotencyKey> _currentProcessed = new();
    private DateTimeOffset _nextRotation;
    private HashSet<IdempotencyKey> _previousInFlight = new();
    private HashSet<IdempotencyKey> _previousProcessed = new();

    public GenerationalIdempotencyGuard(InMemoryIdempotencySettings settings, MessageIdentity identity)
        : this(settings, identity, () => DateTimeOffset.UtcNow)
    {
    }

    /// <param name="now">Seam for deterministic tests of the rotation policy. Production uses the system clock.</param>
    internal GenerationalIdempotencyGuard(InMemoryIdempotencySettings settings, MessageIdentity identity,
        Func<DateTimeOffset> now)
    {
        Settings = settings;
        _identity = identity;
        _now = now;

        // Half the window, because an id has to survive BOTH generations to be forgotten -- rotating on the
        // full window would remember every id for up to twice as long as advertised.
        _rotation = TimeSpan.FromTicks(Math.Max(1, settings.Window.Ticks / 2));
        _generationLimit = Math.Max(1, settings.MaxTracked / 2);
        _nextRotation = now().Add(_rotation);
    }

    public InMemoryIdempotencySettings Settings { get; }

    /// <summary>Number of ids currently remembered as processed, across both generations. Test seam.</summary>
    internal int TrackedCount
    {
        get
        {
            lock (_locker)
            {
                return _currentProcessed.Count + _previousProcessed.Count;
            }
        }
    }

    /// <summary>Number of ids currently claimed but not yet terminal, across both generations. Test seam.</summary>
    internal int InFlightCount
    {
        get
        {
            lock (_locker)
            {
                return _currentInFlight.Count + _previousInFlight.Count;
            }
        }
    }

    public bool TryBeginProcessing(Envelope envelope)
    {
        var key = IdempotencyKey.For(envelope, _identity);

        lock (_locker)
        {
            rotateIfNecessary();

            if (_currentInFlight.Contains(key) || _previousInFlight.Contains(key)
                                               || _currentProcessed.Contains(key) ||
                                               _previousProcessed.Contains(key))
            {
                return false;
            }

            _currentInFlight.Add(key);
            return true;
        }
    }

    public void MarkProcessed(Envelope envelope)
    {
        var key = IdempotencyKey.For(envelope, _identity);

        lock (_locker)
        {
            _currentInFlight.Remove(key);
            _previousInFlight.Remove(key);

            _currentProcessed.Add(key);

            rotateIfNecessary();
        }
    }

    public void Release(Envelope envelope)
    {
        var key = IdempotencyKey.For(envelope, _identity);

        lock (_locker)
        {
            _currentInFlight.Remove(key);
            _previousInFlight.Remove(key);

            // Deliberately also removes any processed record of this id. The failure path means the broker
            // is going to redeliver, and remembering the id would silently discard the retry.
            _currentProcessed.Remove(key);
            _previousProcessed.Remove(key);
        }
    }

    private void rotateIfNecessary()
    {
        var now = _now();
        var byTime = now >= _nextRotation;
        var bySize = _currentProcessed.Count + _currentInFlight.Count >= _generationLimit;

        if (!byTime && !bySize)
        {
            return;
        }

        _previousProcessed = _currentProcessed;
        _currentProcessed = new HashSet<IdempotencyKey>();

        _previousInFlight = _currentInFlight;
        _currentInFlight = new HashSet<IdempotencyKey>();

        _nextRotation = now.Add(_rotation);
    }
}
