using System.Collections.Concurrent;
using DurableComplianceRig.Infrastructure;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.AmazonSqs;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.SqlServer;

namespace DurableComplianceRig.Stores;

/// <summary>
/// One copy of a business intent. <see cref="Id" /> is the copy (and, through <see cref="DeduplicationRig.Stamp" />,
/// its envelope); <see cref="Key" /> is the intent, stamped onto <c>Envelope.DeduplicationId</c> by
/// <c>[DeduplicationIdentity]</c> when the message is routed, so it rides the <c>deduplication-id</c> header on a
/// broker exactly as an application's would.
/// </summary>
public record RigDeduplicatedMessage(Guid Id, [property: DeduplicationIdentity] string Key);

/// <summary>
/// Wiring for the deduplication scenarios: the store's <c>[Deduplicated]</c> handler, the routes for
/// <see cref="RigDeduplicatedMessage" />, and the claim probe. Kept here rather than in the shared profiles so the
/// deduplication scenarios touch nothing the other scenario groups run.
/// </summary>
public static class DeduplicationRig
{
    public static void Configure(WolverineOptions opts, StoreProfile store, TransportProfile transport,
        string queueName, MessageDeduplicationMode mode)
    {
        opts.Durability.MessageDeduplicationMode = mode;
        opts.Services.AddSingleton(new DeduplicationProbe(store.Databases[0], mode));

        // Diagnostic switch: the handlers read the key off the message instead of Envelope.DeduplicationId, which
        // takes the transport's header mapping out of the picture
        var fromMessage = Environment.GetEnvironmentVariable("RIG_DEDUP_KEY_FROM_MESSAGE") == "1";
        opts.Discovery.IncludeType(store switch
        {
            MartenStore => fromMessage ? typeof(MartenDeduplicatedByMemberHandler) : typeof(MartenDeduplicatedHandler),
            EfCoreStore => fromMessage ? typeof(EfCoreDeduplicatedByMemberHandler) : typeof(EfCoreDeduplicatedHandler),
            _ => fromMessage ? typeof(RdbmsDeduplicatedByMemberHandler) : typeof(RdbmsDeduplicatedHandler)
        });

        // Concurrency is the point of several scenarios, so it is stated rather than left to the core count
        switch (transport)
        {
            case LocalQueueTransport:
                opts.LocalQueue(queueName).MaximumParallelMessages(10).CustomizeOutgoing(Stamp);
                opts.PublishMessage<RigDeduplicatedMessage>().ToLocalQueue(queueName);
                break;

            case RabbitMqTransport:
                opts.PublishMessage<RigDeduplicatedMessage>().ToRabbitQueue(queueName).CustomizeOutgoing(Stamp);
                opts.ListenToRabbitQueue(queueName).MaximumParallelMessages(10);
                break;

            case SqsTransport:
                opts.PublishMessage<RigDeduplicatedMessage>().ToSqsQueue(queueName).CustomizeOutgoing(Stamp);
                opts.ListenToSqsQueue(queueName).MaximumParallelMessages(10);
                break;

            case DatabaseQueueTransport when store.Databases[0].Dialect == Dialect.Postgresql:
                opts.PublishMessage<RigDeduplicatedMessage>().ToPostgresqlQueue(queueName.Replace('-', '_'))
                    .CustomizeOutgoing(Stamp);
                break;

            case DatabaseQueueTransport:
                opts.PublishMessage<RigDeduplicatedMessage>().ToSqlServerQueue(queueName.Replace('-', '_'))
                    .CustomizeOutgoing(Stamp);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "No deduplication route for this transport");
        }
    }

    /// <summary>
    /// The same job as <see cref="RigIds.Stamp" />, for the copy: every copy has its own envelope id (the message's),
    /// so the inbox and dead letter rows of each copy can be told apart, while all copies share one deduplication id.
    /// Runs after RigIds.Stamp, which leaves an envelope it does not recognise alone.
    /// </summary>
    public static void Stamp(Envelope envelope)
    {
        if (envelope.Message is RigDeduplicatedMessage message) envelope.Id = message.Id;
    }
}

/// <summary>Reads the deduplication table directly, from its own connection.</summary>
public class DeduplicationProbe(RigDatabase database, MessageDeduplicationMode mode)
{
    public string Table => $"{database.Schema}." + (mode == MessageDeduplicationMode.CompareByHash
        ? "wolverine_deduplication_hashed"
        : "wolverine_deduplication");

    /// <summary>
    /// Claim rows whose readable id is <paramref name="key" />, as another connection sees them. On SQL Server
    /// <c>READPAST</c> skips a row an open transaction still holds, so an uncommitted claim reads as absent there just
    /// as it does under PostgreSQL's MVCC -- instead of blocking until that transaction ends.
    /// </summary>
    public async Task<int> CommittedClaimsAsync(string key)
    {
        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        var hint = database.Dialect == Dialect.SqlServer ? " with (readpast)" : "";

        // Binary comparison of the readable id, so the probe counts exactly what was written under either collation
        cmd.CommandText = database.Dialect == Dialect.SqlServer
            ? $"select count(*) from {Table}{hint} where cast(deduplication_id as varbinary(1000)) = cast(cast(@id as varchar(250)) as varbinary(1000))"
            : $"select count(*) from {Table} where deduplication_id = @id";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = key;
        cmd.Parameters.Add(parameter);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>Every claim row whose readable id ends with <paramref name="suffix" />: one spec's claims.</summary>
    public async Task<IReadOnlyList<string>> ClaimsEndingWithAsync(string suffix)
    {
        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select deduplication_id from {Table} where deduplication_id like @suffix";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "suffix";
        parameter.Value = "%" + suffix;
        cmd.Parameters.Add(parameter);

        var claims = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) claims.Add(reader.GetString(0));
        return claims;
    }

    /// <summary>The dead letter's exception, for the narration: the rig's snapshot keeps only its type.</summary>
    public async Task<string?> DeadLetterExceptionAsync(Guid id)
    {
        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select exception_type, exception_message from {database.Schema}.wolverine_dead_letters where id = @id";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        cmd.Parameters.Add(parameter);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? $"{reader.GetValue(0)}: {reader.GetValue(1)}" : null;
    }

    /// <summary>Diagnostic only: give back a claim by hand, as a release that never ran would have.</summary>
    public async Task DeleteClaimsEndingWithAsync(string suffix)
    {
        await using var conn = database.Connect();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"delete from {Table} where deduplication_id like @suffix";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "suffix";
        parameter.Value = "%" + suffix;
        cmd.Parameters.Add(parameter);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Deduplication-specific handler behaviour that <see cref="Script" /> does not model: holding a copy before its
/// handler body (after the claim, so the claim is held too), and probing whether the claim is visible to another
/// connection while the handler runs. Static for the same reason Script is.
/// </summary>
public static class DeduplicationHooks
{
    // Armed holds wait here for the copy's first run, which moves its hold to _heldNow until LetGo
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _holds = new();
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource> _heldNow = new();
    private static readonly ConcurrentDictionary<Guid, int> _claimsSeenWhileRunning = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _crashedNodes = new();

    public static volatile bool ProbeClaims;

    public static void Reset()
    {
        foreach (var hold in _holds.Values.Concat(_heldNow.Values)) hold.TrySetResult();
        _holds.Clear();
        _heldNow.Clear();
        _claimsSeenWhileRunning.Clear();
        foreach (var crash in _crashedNodes.Values) crash.Cancel();
        _crashedNodes.Clear();
        ProbeClaims = false;
    }

    /// <summary>The copy's first run waits in its handler, claim taken, until <see cref="LetGo" />.</summary>
    public static void HoldFirstRun(Guid id) =>
        _holds[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void LetGo(Guid id)
    {
        if (_holds.TryRemove(id, out var armed)) armed.TrySetResult();
        if (_heldNow.TryGetValue(id, out var held)) held.TrySetResult();
    }

    public static bool IsHeld(Guid id) => _heldNow.ContainsKey(id);

    /// <summary>Committed claim rows for the copy's key, as another connection saw them while its handler ran.</summary>
    public static int? ClaimsSeenWhileRunning(Guid id) =>
        _claimsSeenWhileRunning.TryGetValue(id, out var count) ? count : null;

    /// <summary>The "crash" signal: a handler held on this node stops holding.</summary>
    public static void NodeCrashed(string node) => crashFor(node).Cancel();

    private static CancellationTokenSource crashFor(string node) => _crashedNodes.GetOrAdd(node, _ => new CancellationTokenSource());

    /// <summary>Called by every deduplicated rig handler first thing, which is after the claim.</summary>
    /// <param name="onCrash">
    /// A disposed host does not kill its in-flight handlers. Where the claim is a row lock held by the handler's own
    /// open transaction (EF Core), that zombie would block the recovered copy's claim for as long as it waits, which a
    /// real crash never does: the database rolls a dead connection back. So that handler closes its connection when
    /// its node "crashes", and keeps waiting. It must not throw: the zombie runtime would run its error policy and
    /// dead letter a message a real crash leaves alone, and on the claim-and-release path a throw would also run the
    /// compensating release that a real crash never gets to run. The other stores hold nothing open while waiting.
    /// </param>
    public static async Task BeforeHandlingAsync(RigDeduplicatedMessage message, RigNodeName node, DeduplicationProbe probe,
        Func<Task>? onCrash = null)
    {
        if (ProbeClaims && !_claimsSeenWhileRunning.ContainsKey(message.Id))
        {
            _claimsSeenWhileRunning[message.Id] = await probe.CommittedClaimsAsync(message.Key);
        }

        if (!_holds.TryRemove(message.Id, out var hold)) return;

        _heldNow[message.Id] = hold;
        try
        {
            var crashed = crashFor(node.Name).Token;
            if (onCrash != null)
            {
                await using var registration = crashed.Register(() => _ = onCrash());
                await hold.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
            else
            {
                await hold.Task.WaitAsync(TimeSpan.FromMinutes(2));
            }
        }
        finally
        {
            _heldNow.TryRemove(message.Id, out _);
        }
    }
}

/// <summary>The plain RDBMS stores: the claim is written on its own connection and released if the handler throws.</summary>
public static class RdbmsDeduplicatedHandler
{
    [Deduplicated]
    public static Task Handle(RigDeduplicatedMessage message, Envelope envelope, IMessageContext context,
        RigNodeName node, RdbmsLedger ledger, DeduplicationProbe probe) =>
        HandleAsync(message, envelope, context, node, ledger, probe);

    internal static async Task HandleAsync(RigDeduplicatedMessage message, Envelope envelope, IMessageContext context,
        RigNodeName node, RdbmsLedger ledger, DeduplicationProbe probe)
    {
        await DeduplicationHooks.BeforeHandlingAsync(message, node, probe);
        await ledger.RecordAsync(context, envelope, [message.Id], node);
    }
}

public static class RdbmsDeduplicatedByMemberHandler
{
    [Deduplicated(ValueSource.InputMember, nameof(RigDeduplicatedMessage.Key))]
    public static Task Handle(RigDeduplicatedMessage message, Envelope envelope, IMessageContext context,
        RigNodeName node, RdbmsLedger ledger, DeduplicationProbe probe) =>
        RdbmsDeduplicatedHandler.HandleAsync(message, envelope, context, node, ledger, probe);
}

/// <summary>Marten: the claim is queued onto the session and commits with the ledger row (GH-4505).</summary>
public static class MartenDeduplicatedHandler
{
    [Deduplicated]
    public static Task Handle(RigDeduplicatedMessage message, Envelope envelope, IDocumentSession session,
        IMessageContext context, RigNodeName node, MartenLedger ledger, DeduplicationProbe probe) =>
        HandleAsync(message, envelope, session, context, node, ledger, probe);

    internal static async Task HandleAsync(RigDeduplicatedMessage message, Envelope envelope, IDocumentSession session,
        IMessageContext context, RigNodeName node, MartenLedger ledger, DeduplicationProbe probe)
    {
        await DeduplicationHooks.BeforeHandlingAsync(message, node, probe);
        await ledger.RecordAsync(session, context, envelope, [message.Id], node);
    }
}

public static class MartenDeduplicatedByMemberHandler
{
    [Deduplicated(ValueSource.InputMember, nameof(RigDeduplicatedMessage.Key))]
    public static Task Handle(RigDeduplicatedMessage message, Envelope envelope, IDocumentSession session,
        IMessageContext context, RigNodeName node, MartenLedger ledger, DeduplicationProbe probe) =>
        MartenDeduplicatedHandler.HandleAsync(message, envelope, session, context, node, ledger, probe);
}

/// <summary>
/// EF Core: the claim is an INSERT on the DbContext's own transaction, before the handler runs (GH-5108). Mirrors
/// <see cref="EfCoreRigHandlers" />, whose body is private.
/// </summary>
public static class EfCoreDeduplicatedHandler
{
    [Deduplicated]
    public static Task Handle(RigDeduplicatedMessage message, Envelope envelope, RigDbContext db, RigNodeName node,
        DeduplicationProbe probe) => HandleAsync(message, envelope, db, node, probe);

    internal static async Task HandleAsync(RigDeduplicatedMessage message, Envelope envelope, RigDbContext db,
        RigNodeName node, DeduplicationProbe probe)
    {
        await DeduplicationHooks.BeforeHandlingAsync(message, node, probe,
            () => db.Database.GetDbConnection().CloseAsync());

        db.Envelope = envelope;
        var (step, execution) = await Script.BeginAsync(message.Id, node.Name, envelope.Id);
        db.Pending.Add(execution);
        db.Ledger.Add(new LedgerEntry
        {
            MessageId = execution.MessageId,
            Attempt = execution.Attempt,
            Node = execution.Node,
            Tenant = envelope.TenantId,
            StartedAt = execution.StartedAt,
            RecordedAt = DateTimeOffset.UtcNow
        });

        try
        {
            Script.BeforeCommit(step, execution);
        }
        catch (Exception e)
        {
            db.FinishPending($"failed: {e.GetType().Name}");
            throw;
        }
    }
}

public static class EfCoreDeduplicatedByMemberHandler
{
    [Deduplicated(ValueSource.InputMember, nameof(RigDeduplicatedMessage.Key))]
    public static Task Handle(RigDeduplicatedMessage message, Envelope envelope, RigDbContext db, RigNodeName node,
        DeduplicationProbe probe) => EfCoreDeduplicatedHandler.HandleAsync(message, envelope, db, node, probe);
}
