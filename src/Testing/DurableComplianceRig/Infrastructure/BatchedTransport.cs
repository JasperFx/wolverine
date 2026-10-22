using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime.Batching;

namespace DurableComplianceRig.Infrastructure;

/// <summary>
/// A transport whose <see cref="RigItem" />s are batched with <c>BatchMessagesOf&lt;RigItem&gt;()</c> and handled as
/// <c>RigItem[]</c>. Wraps an ordinary <see cref="TransportProfile" />, which still decides where the items travel and
/// how they are listened to; this adds the batch and says where it executes.
/// </summary>
public class BatchedTransport : TransportProfile
{
    private readonly TransportProfile _inner;
    private readonly List<Action<BatchingOptions>> _batching = [];

    /// <param name="inner">Where the items travel</param>
    /// <param name="durableBatchQueue">
    /// Make the local queue the batches execute on durable. For a local inner transport the batch executes on the
    /// items' own durable queue; for a broker it executes on the element type's default local queue
    /// </param>
    public BatchedTransport(TransportProfile inner, bool durableBatchQueue)
    {
        _inner = inner;
        DurableBatchQueue = durableBatchQueue;
    }

    public bool DurableBatchQueue { get; }

    /// <summary>True when the items arrive on a broker listener rather than a local queue.</summary>
    public bool ItemsArriveFromABroker => _inner is not LocalQueueTransport;

    public override string Name => _inner is LocalQueueTransport
        ? "batches on a durable local queue"
        : $"{_inner.Name}, batched on {(DurableBatchQueue ? "a durable" : "the default buffered")} local queue";

    public override string Key => _inner.Key + "-batched";

    /// <summary>The local queue the batches execute on, once a node has started.</summary>
    public string? BatchQueueName { get; private set; }

    /// <summary>
    /// Batch size and trigger time first: every scenario wants its items in ONE batch, so a scripted failure
    /// decides the fate of every member, and the outcome each item is expected to reach does not depend on timing.
    /// </summary>
    public void ConfigureBatch(Action<BatchingOptions> configure) => _batching.Add(configure);

    public override void Configure(WolverineOptions opts, StoreProfile store, string queueName)
    {
        _inner.Configure(opts, store, queueName);

        var queue = opts.BatchMessagesOf<RigItem>(batch =>
        {
            batch.BatchSize = 100;
            batch.TriggerTime = 1.Seconds();

            // Topology (a): the batch executes on the very durable queue its items were sent to
            if (_inner is LocalQueueTransport) batch.LocalExecutionQueueName = queueName;

            foreach (var configure in _batching) configure(batch);

            BatchQueueName = batch.LocalExecutionQueueName;
        });

        if (DurableBatchQueue) queue.UseDurableInbox();
    }
}

/// <summary>
/// Batch behaviour the per-item <see cref="Script" /> cannot express, because it needs the item instances themselves:
/// <see cref="ApplyItemException" /> names its poison items by reference.
/// </summary>
public static class BatchItemScript
{
    private static readonly ConcurrentDictionary<Guid, bool> _poison = new();
    private static int _poisonThrows;

    public static void Reset()
    {
        _poison.Clear();
        _poisonThrows = 0;
    }

    /// <summary>Every batch holding this item throws <see cref="ApplyItemException" /> naming it, the rest replayed.</summary>
    public static void PoisonForApplyItem(Guid id) => _poison[id] = true;

    /// <summary>How many times a batch handler has thrown <see cref="ApplyItemException" /> so far.</summary>
    public static int PoisonThrows => _poisonThrows;

    /// <summary>Called by every store's <c>RigItem[]</c> handler before it touches the ledger.</summary>
    public static void ThrowIfAnyIsPoison(RigItem[] items)
    {
        if (_poison.IsEmpty) return;

        var poison = items.Where(x => _poison.ContainsKey(x.Id)).Cast<object>().ToArray();
        if (poison.Length == 0) return;

        Interlocked.Increment(ref _poisonThrows);
        throw ApplyItemException.DeadLetterAndReplayOthers(poison);
    }
}

/// <summary>
/// Captures every Error and Critical log entry a rig node writes, so a scenario can tell "the invariants hold" apart
/// from "the invariants hold while Wolverine retries a failing write forever in the background".
/// </summary>
public sealed class RigLogCapture : ILoggerProvider
{
    public record Entry(string Category, LogLevel Level, string Message, Exception? Exception)
    {
        public IEnumerable<Exception> Chain()
        {
            for (var e = Exception; e != null; e = e.InnerException) yield return e;
        }

        public override string ToString() =>
            $"{Level} {Category}: {Message}{(Exception == null ? "" : $" [{Exception.GetType().Name}: {Exception.Message}]")}";
    }

    private static readonly ConcurrentQueue<Entry> _entries = new();

    public static IReadOnlyList<Entry> Entries => _entries.ToArray();

    public static void Reset() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _entries.Enqueue(new Entry(category, logLevel, formatter(state, exception), exception));
        }
    }
}
