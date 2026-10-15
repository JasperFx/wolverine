using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.Durability;

/// <summary>
///     GH-5028. The common shape of the inbox steps of a durability pass: one batch of rows per call, each
///     executed inline. A row that nothing settled is left as it was and not tried again in this pass
/// </summary>
public abstract class InboxDurabilityPassStep : IDurabilityPassStep
{
    private readonly IMessageStore _store;
    private readonly ILogger _logger;
    private readonly HashSet<Guid> _failed = new();
    private DurabilityPassInboxExecutor? _executor;
    private int _remaining;

    protected InboxDurabilityPassStep(IMessageStore store, IDurabilityPassInboxSource source, ILogger logger)
    {
        _store = store;
        Source = source;
        _logger = logger;
    }

    protected IDurabilityPassInboxSource Source { get; }

    public abstract string Name { get; }

    protected abstract Task<IReadOnlyList<Envelope>> loadAsync(DurabilityPassContext context, Guid[] excluded,
        CancellationToken token);

    protected abstract Task<int> countAsync(DurabilityPassContext context, Guid[] excluded,
        CancellationToken token);

    protected abstract DurabilityPassStepResult buildResult(int settled, int remaining);

    public async Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        if (_failed.Count > DurabilityPassContext.MaxExcludedIds)
        {
            // Nothing done, so the pass moves on. What is left waits for a later pass. See GH-5035
            _logger.LogWarning("Durability pass step '{Step}' stopped against {Store} after {Failed} failed",
                Name, _store.Uri, _failed.Count);
            return buildResult(0, _remaining);
        }

        var page = await loadAsync(context, _failed.ToArray(), token);
        if (page.Count == 0) return DurabilityPassStepResult.Nothing;

        _executor ??= new DurabilityPassInboxExecutor(context.Runtime, _store, _logger);

        var settled = 0;
        foreach (var envelope in page)
        {
            if (await _executor.ExecuteAsync(envelope, token) == InboxExecutionOutcome.Settled)
            {
                settled++;
            }
            else
            {
                _failed.Add(envelope.Id);
            }
        }

        // Past the cap, the failed rows are counted as left too rather than sending a longer id list
        var excluded = _failed.Count > DurabilityPassContext.MaxExcludedIds ? [] : _failed.ToArray();
        var remaining = page.Count < context.BatchSize ? 0 : await countAsync(context, excluded, token);
        _remaining = remaining;

        _logger.LogInformation(
            "Durability pass step '{Step}' executed {Settled} inbox message(s) from {Store} and left {Failed} that failed",
            Name, settled, _store.Uri, page.Count - settled);

        return buildResult(settled, remaining);
    }
}

/// <summary>
///     GH-5028. Releases the due scheduled messages in a <see cref="DurabilityMode.ServerlessWithOutbox" />
///     durability pass. There are no local queues and no scheduled job polling in that mode, so each due row is
///     executed inline by the pass: a scheduled send to an external endpoint is forwarded to it, and a local
///     message, such as a scheduled retry, is handled
/// </summary>
public sealed class ReleaseScheduledMessagesStep : InboxDurabilityPassStep
{
    public ReleaseScheduledMessagesStep(IMessageStore store, IDurabilityPassInboxSource source, ILogger logger)
        : base(store, source, logger)
    {
    }

    public override string Name => "Release due scheduled messages";

    protected override Task<IReadOnlyList<Envelope>> loadAsync(DurabilityPassContext context, Guid[] excluded,
        CancellationToken token)
    {
        return Source.LoadDueScheduledAsync(context.Now, excluded, context.BatchSize, token);
    }

    protected override Task<int> countAsync(DurabilityPassContext context, Guid[] excluded,
        CancellationToken token)
    {
        return Source.CountDueScheduledAsync(context.Now, excluded, token);
    }

    protected override DurabilityPassStepResult buildResult(int settled, int remaining)
    {
        return new DurabilityPassStepResult { ScheduledReleased = settled, Remaining = remaining };
    }
}

/// <summary>
///     GH-5028. Recovers the incoming messages that were persisted but never handled in a
///     <see cref="DurabilityMode.ServerlessWithOutbox" /> durability pass, executing each one inline. When
///     <see cref="DurabilitySettings.InboxStaleTime" /> is set, only rows whose stale clock has run out are taken,
///     so a pass never races the request that is still handling a row
/// </summary>
public sealed class RecoverIncomingMessagesStep : InboxDurabilityPassStep
{
    public RecoverIncomingMessagesStep(IMessageStore store, IDurabilityPassInboxSource source, ILogger logger)
        : base(store, source, logger)
    {
    }

    public override string Name => "Recover incoming messages";

    private static DateTimeOffset? staleBefore(DurabilityPassContext context)
    {
        var staleTime = context.Runtime.Options.Durability.InboxStaleTime;
        return staleTime.HasValue ? context.Now.Subtract(staleTime.Value) : null;
    }

    protected override Task<IReadOnlyList<Envelope>> loadAsync(DurabilityPassContext context, Guid[] excluded,
        CancellationToken token)
    {
        return Source.LoadRecoverableIncomingAsync(staleBefore(context), excluded, context.BatchSize, token);
    }

    protected override Task<int> countAsync(DurabilityPassContext context, Guid[] excluded,
        CancellationToken token)
    {
        return Source.CountRecoverableIncomingAsync(staleBefore(context), excluded, token);
    }

    protected override DurabilityPassStepResult buildResult(int settled, int remaining)
    {
        return new DurabilityPassStepResult { IncomingRecovered = settled, Remaining = remaining };
    }
}
