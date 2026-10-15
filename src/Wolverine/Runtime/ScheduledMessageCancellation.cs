namespace Wolverine.Runtime;

internal sealed class ScheduledMessageCancellationRegistration
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _cancellationLock = new(1, 1);
    private readonly ScheduledMessageCancellationStatus? _initialStatus;
    private Func<CancellationToken, ValueTask<ScheduledMessageCancellationStatus>>? _cancel;
    private ScheduledMessageCancellationStatus? _terminalStatus;

    public ScheduledMessageCancellationRegistration(Guid envelopeId,
        ScheduledMessageCancellationStatus? initialStatus = null)
    {
        EnvelopeId = envelopeId;
        _initialStatus = initialStatus;
    }

    public Guid EnvelopeId { get; }

    public bool CanBeBound
    {
        get
        {
            lock (_lock) return _initialStatus == null && _cancel == null && _terminalStatus == null;
        }
    }

    public bool IsBound
    {
        get
        {
            lock (_lock) return _cancel != null;
        }
    }

    public bool TryBind(Func<CancellationToken, ValueTask<ScheduledMessageCancellationStatus>> cancel)
    {
        lock (_lock)
        {
            if (_initialStatus != null || _cancel != null || _terminalStatus != null)
            {
                return false;
            }

            _cancel = cancel;
        }

        return true;
    }

    public void MarkNotScheduled()
    {
        lock (_lock)
        {
            if (_initialStatus == null && _cancel == null)
            {
                _terminalStatus = ScheduledMessageCancellationStatus.NotScheduled;
            }
        }
    }

    public async ValueTask<ScheduledMessageCancellationResult> CancelAsync(CancellationToken cancellationToken)
    {
        if (_initialStatus is { } initialStatus)
        {
            return new ScheduledMessageCancellationResult(EnvelopeId, initialStatus);
        }

        await _cancellationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Func<CancellationToken, ValueTask<ScheduledMessageCancellationStatus>>? cancel;
            lock (_lock)
            {
                if (_terminalStatus is { } terminalStatus)
                {
                    return new ScheduledMessageCancellationResult(EnvelopeId, terminalStatus);
                }

                cancel = _cancel;
            }

            if (cancel == null)
            {
                return new ScheduledMessageCancellationResult(EnvelopeId,
                    ScheduledMessageCancellationStatus.NotScheduledYet);
            }

            var status = await cancel(cancellationToken).ConfigureAwait(false);
            if (status is ScheduledMessageCancellationStatus.CancellationAccepted or
                ScheduledMessageCancellationStatus.NoLongerScheduled)
            {
                lock (_lock)
                {
                    _terminalStatus = status;
                }
            }

            return new ScheduledMessageCancellationResult(EnvelopeId, status);
        }
        finally
        {
            _cancellationLock.Release();
        }
    }
}

internal sealed class ScheduledMessageCancellationHandle : IScheduledMessage
{
    private readonly IReadOnlyList<ScheduledMessageCancellationRegistration> _registrations;

    public ScheduledMessageCancellationHandle(IEnumerable<ScheduledMessageCancellationRegistration> registrations)
    {
        _registrations = registrations.ToArray();
        EnvelopeIds = Array.AsReadOnly(_registrations.Select(x => x.EnvelopeId).ToArray());
    }

    public IReadOnlyList<Guid> EnvelopeIds { get; }

    public async ValueTask<IReadOnlyList<ScheduledMessageCancellationResult>> CancelAsync(
        CancellationToken cancellationToken = default)
    {
        var cancellations = _registrations.Select(x => x.CancelAsync(cancellationToken).AsTask()).ToArray();
        return await Task.WhenAll(cancellations).ConfigureAwait(false);
    }
}

internal sealed class ScheduledMessageCancellationRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid,
        WeakReference<ScheduledMessageCancellationRegistration>> _registrations = new();
    private int _registeredCount;

    public void Register(ScheduledMessageCancellationRegistration registration)
    {
        _registrations[registration.EnvelopeId] = new WeakReference<ScheduledMessageCancellationRegistration>(registration);

        if (Interlocked.Increment(ref _registeredCount) % 64 == 0)
        {
            foreach (var (id, reference) in _registrations)
            {
                if (!reference.TryGetTarget(out _))
                {
                    Remove(id);
                }
            }
        }
    }

    public ScheduledMessageCancellationRegistration? Find(Guid envelopeId)
    {
        if (_registrations.TryGetValue(envelopeId, out var reference) && reference.TryGetTarget(out var registration))
        {
            return registration;
        }

        Remove(envelopeId);
        return null;
    }

    public void Remove(Guid envelopeId)
    {
        _registrations.TryRemove(envelopeId, out _);
    }
}
