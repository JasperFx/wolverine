using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

public partial class MultiTenantedMessageStore : IMessageStore, IMessageInbox, IMessageOutbox, IMessageStoreAdmin,
    IDeadLetters, ISagaSupport, INodeAgentPersistence
{
    private readonly ILogger _logger;
    private readonly RetryBlock<IEnvelopeCommand> _retryBlock;
    private readonly IWolverineRuntime _runtime;

    private ImHashMap<string, IMessageStore> _byTenant = ImHashMap<string, IMessageStore>.Empty;
    private bool _initialized;


    public MultiTenantedMessageStore(IMessageStore main, IWolverineRuntime runtime,
        ITenantedMessageSource source)
    {
        _logger = runtime.LoggerFactory.CreateLogger<MultiTenantedMessageStore>();
        _runtime = runtime;
        Source = source;

        _retryBlock = new RetryBlock<IEnvelopeCommand>((command, cancellation) => command.ExecuteAsync(cancellation),
            _logger, runtime.Cancellation);

        Main = main;
    }

    public List<string> TenantIds { get; } = new();

    public void DemoteToAncillary()
    {
        Main.DemoteToAncillary();
    }

    public MessageStoreRole Role => MessageStoreRole.Composite;

    public ITenantedMessageSource Source { get; }

    public Uri Uri => new($"{PersistenceConstants.AgentScheme}://multitenanted");

    public IMessageStore Main { get; }

    public Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range, CancellationToken token)
    {
        throw new NotSupportedException();
    }

    public Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        throw new NotSupportedException();
    }

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        throw new NotSupportedException();
    }

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        throw new NotSupportedException();
    }

    public Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        throw new NotSupportedException();
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        if (tenantId is not null)
        {
            var database = await GetDatabaseAsync(tenantId);
            return await database.DeadLetters.DeadLetterEnvelopeByIdAsync(id);
        }

        foreach (var database in databases())
        {
            var deadLetterEnvelope = await database.DeadLetters.DeadLetterEnvelopeByIdAsync(id);
            if (deadLetterEnvelope != null)
            {
                return deadLetterEnvelope;
            }
        }

        return null;
    }

    async Task IMessageInbox.ScheduleExecutionAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.ScheduleExecutionAsync(envelope);
    }

    async Task IMessageInbox.MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
    }

    async Task IMessageInbox.IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);
    }

    async Task IMessageInbox.StoreIncomingAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);

        // The main store's failures propagate untouched: the receiver pauses for inbox recovery on
        // those, which is the behavior this path has always had.
        if (ReferenceEquals(database, Main))
        {
            await database.Inbox.StoreIncomingAsync(envelope);
            return;
        }

        try
        {
            await database.Inbox.StoreIncomingAsync(envelope);
        }
        catch (DuplicateIncomingEnvelopeException)
        {
            // GH-4435. Never wrap this -- DurableReceiver's deduplication path keys off the exact type.
            throw;
        }
        catch (Exception e)
        {
            // GH-4435. Mark the failure tenant-scoped so the receiver defers this one envelope back to
            // the broker instead of pausing a listener that serves every other tenant.
            throw new TenantedInboxWriteException([envelope], false, [e]);
        }
    }

    async Task IMessageInbox.StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        // GH-4435. Group by the RESOLVED store rather than by tenant id. Many tenant ids map to one
        // database -- null/"*default*"/"main" all resolve to Main -- and grouping by tenant id pushed
        // those batches down the multi-group path even when a single database owned every envelope.
        var groups = new Dictionary<IMessageStore, List<Envelope>>();
        var failures = new List<Exception>();
        var unpersisted = new List<Envelope>();

        foreach (var byTenant in envelopes.GroupBy(x => x.TenantId))
        {
            IMessageStore store;

            try
            {
                store = await GetDatabaseAsync(byTenant.Key);
            }
            catch (Exception e)
            {
                // GH-4435. This used to log-and-skip UnknownTenantIdException, which meant envelopes for an
                // unresolvable tenant were never stored AND never reported -- the caller took the clean
                // return as success and acked them, so they were simply gone. An unresolvable tenant is now
                // a tenant-scoped failure like any other: the envelopes are deferred back to the broker and
                // the misconfiguration stays visible instead of eating messages.
                //
                // Catching the typed exception never covered the common case anyway: a StaticTenantSource
                // throws ArgumentOutOfRangeException for an unregistered tenant, not UnknownTenantIdException.
                //
                // Resolution also reaches the network on its own (Source.FindAsync, and the MigrateAsync
                // behind it), so a genuine outage can surface here rather than on the write below.
                _logger.LogError(e,
                    "Unable to resolve the message store for tenant {TenantId}; {Count} incoming envelope(s) were not stored",
                    byTenant.Key, byTenant.Count());

                failures.Add(e);
                unpersisted.AddRange(byTenant);
                continue;
            }

            if (groups.TryGetValue(store, out var list))
            {
                list.AddRange(byTenant);
            }
            else
            {
                groups[store] = byTenant.ToList();
            }
        }

        if (failures.Count == 0 && groups.Count == 1)
        {
            // One database owns the whole batch. Await it directly and let everything it throws --
            // DuplicateIncomingEnvelopeException included -- reach the caller untouched.
            var single = groups.First();
            await single.Key.Inbox.StoreIncomingAsync(single.Value);
            return;
        }

        var duplicates = new List<Envelope>();
        var includesMainStore = false;

        foreach (var pair in groups)
        {
            try
            {
                await pair.Key.Inbox.StoreIncomingAsync(pair.Value);

                // GH-4435. The caller decides what to ack from the exception below, and it re-runs the
                // whole batch through the per-envelope path. An envelope whose group DID commit must not
                // be stored a second time there -- that reads as a duplicate, and a duplicate is settled
                // at the listener WITHOUT ever being handled. See DurableReceiver.receiveOneAsync.
                foreach (var envelope in pair.Value) envelope.WasPersistedInInbox = true;
            }
            catch (DuplicateIncomingEnvelopeException e)
            {
                // A store's batched insert is all-or-nothing, so nothing in this group landed.
                duplicates.AddRange(e.Duplicates);
                unpersisted.AddRange(pair.Value);
            }
            catch (Exception e)
            {
                failures.Add(e);
                unpersisted.AddRange(pair.Value);

                if (ReferenceEquals(pair.Key, Main))
                {
                    includesMainStore = true;
                }
            }
        }

        // GH-4435. These used to be posted to a RetryBlock, which never rethrows: the caller saw a clean
        // return, acked the whole batch, and handed envelopes that had never been stored to the handler
        // pipeline. A failure has to reach DurableReceiver for it to settle anything correctly.
        if (failures.Count > 0)
        {
            throw new TenantedInboxWriteException(unpersisted, includesMainStore, failures);
        }

        if (duplicates.Count > 0)
        {
            throw new DuplicateIncomingEnvelopeException(duplicates);
        }
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        return await database.Inbox.ExistsAsync(envelope, cancellation);
    }

    async Task IMessageInbox.RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);
    }

    async Task IMessageInbox.MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);
    }

    // GH-4435. Group by the RESOLVED store, and stop swallowing a tenant that cannot be resolved. A
    // skipped group left its rows Incoming, owned by a node that had already handled them, so the
    // recovery sweep re-offered the messages and they were handled twice -- duplicate work reported to
    // the caller as success. (The log message here even said "store incoming envelopes", copy-pasted
    // from StoreIncomingAsync, so the one clue it did leave pointed at the wrong method.)
    //
    // Safe to propagate, because both callers already expect it: InboxCompletionCoalescer documents this
    // as "May throw; a failure falls back to markOne per envelope", and DurableReceiver's _markAsHandled
    // RetryBlock retries. Per-envelope is the correct fallback here -- it resolves each tenant on its own.
    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var groups = new Dictionary<IMessageStore, List<Envelope>>();
        var failures = new List<Exception>();

        foreach (var byTenant in envelopes.GroupBy(x => x.TenantId))
        {
            IMessageStore store;

            try
            {
                store = await GetDatabaseAsync(byTenant.Key);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Unable to resolve the message store for tenant {TenantId}; {Count} envelope(s) were not marked as handled",
                    byTenant.Key, byTenant.Count());
                failures.Add(e);
                continue;
            }

            if (groups.TryGetValue(store, out var list))
            {
                list.AddRange(byTenant);
            }
            else
            {
                groups[store] = byTenant.ToList();
            }
        }

        if (failures.Count == 0 && groups.Count == 1)
        {
            var single = groups.First();
            await single.Key.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelopes);
            return;
        }

        foreach (var pair in groups)
        {
            try
            {
                await pair.Key.Inbox.MarkIncomingEnvelopeAsHandledAsync(pair.Value);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        if (failures.Count > 0)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    Task IMessageInbox.ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        return executeOnAllAsync(d => d.Inbox.ReleaseIncomingAsync(ownerId, receivedAt));
    }

    Task<IReadOnlyList<Envelope>> IMessageOutbox.LoadOutgoingAsync(Uri destination)
    {
        throw new NotSupportedException();
    }

    async Task IMessageOutbox.StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Outbox.StoreOutgoingAsync(envelope, ownerId);
    }

    // GH-4319 split a coalesced outbox batch by tenant so it never reached the wrong database. GH-4435
    // finishes the job: group by the RESOLVED store (many tenants share one database), and stop
    // swallowing a tenant that cannot be resolved. That skip was the worst of this family -- the
    // outgoing envelopes were never persisted, so the messages were simply never sent, and the caller
    // was told the write succeeded.
    //
    // Safe to propagate: the caller is DurableSendingAgent's EnvelopeStoreCoalescer, which catches a
    // failed batch and falls back to storing one envelope at a time (see coalesced_envelope_storage_4319),
    // and that per-envelope path resolves each tenant on its own.
    async Task IMessageOutbox.StoreOutgoingAsync(IReadOnlyList<Envelope> envelopes, int ownerId)
    {
        var groups = new Dictionary<IMessageStore, List<Envelope>>();
        var failures = new List<Exception>();

        foreach (var byTenant in envelopes.GroupBy(x => x.TenantId))
        {
            IMessageStore store;

            try
            {
                store = await GetDatabaseAsync(byTenant.Key);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Unable to resolve the message store for tenant {TenantId}; {Count} outgoing envelope(s) were not stored",
                    byTenant.Key, byTenant.Count());
                failures.Add(e);
                continue;
            }

            if (groups.TryGetValue(store, out var list))
            {
                list.AddRange(byTenant);
            }
            else
            {
                groups[store] = byTenant.ToList();
            }
        }

        if (failures.Count == 0 && groups.Count == 1)
        {
            var single = groups.First();
            await single.Key.Outbox.StoreOutgoingAsync(envelopes, ownerId);
            return;
        }

        foreach (var pair in groups)
        {
            try
            {
                await pair.Key.Outbox.StoreOutgoingAsync(pair.Value, ownerId);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        if (failures.Count > 0)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    // GH-4435. The outbox twin of StoreIncomingAsync, and it had the same defect: each group went to the
    // _retryBlock, which never rethrows, so a delete that never happened was reported as success. An
    // undeleted outgoing row is re-sent by recovery, so the symptom here is duplicate delivery rather
    // than loss -- quieter, but the same swallow.
    //
    // Propagating is safe precisely BECAUSE every caller already retries: DurableSendingAgent wraps this
    // in its own RetryBlock<Envelope[]> and in executeWithRetriesAsync. The inner block was not a second
    // line of defense, it was defeating the caller's.
    async Task IMessageOutbox.DeleteOutgoingAsync(Envelope[] envelopes)
    {
        var groups = new Dictionary<IMessageStore, List<Envelope>>();
        var failures = new List<Exception>();

        foreach (var byTenant in envelopes.GroupBy(x => x.TenantId))
        {
            IMessageStore store;

            try
            {
                store = await GetDatabaseAsync(byTenant.Key);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Unable to resolve the message store for tenant {TenantId} while deleting outgoing envelopes",
                    byTenant.Key);
                failures.Add(e);
                continue;
            }

            if (groups.TryGetValue(store, out var list))
            {
                list.AddRange(byTenant);
            }
            else
            {
                groups[store] = byTenant.ToList();
            }
        }

        if (failures.Count == 0 && groups.Count == 1)
        {
            var single = groups.First();
            await single.Key.Outbox.DeleteOutgoingAsync(envelopes);
            return;
        }

        foreach (var pair in groups)
        {
            try
            {
                await pair.Key.Outbox.DeleteOutgoingAsync(pair.Value.ToArray());
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        if (failures.Count > 0)
        {
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }

    async Task IMessageOutbox.DeleteOutgoingAsync(Envelope envelope)
    {
        var database = await GetDatabaseAsync(envelope.TenantId);
        await database.Outbox.DeleteOutgoingAsync(envelope);
    }

    async Task IMessageOutbox.DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        var discardGroups = discards.GroupBy(x => x.TenantId ?? TransportConstants.Default).ToArray();
        var reassignedGroups = reassigned.GroupBy(x => x.TenantId ?? TransportConstants.Default).ToArray();

        var dict = new Dictionary<string, DiscardAndReassignOutgoingAsyncGroup>();

        foreach (var group in discardGroups)
        {
            try
            {
                var database = await GetDatabaseAsync(group.Key);
                var command = new DiscardAndReassignOutgoingAsyncGroup(database, nodeId);
                dict[group.Key] = command;

                command.AddDiscards(group);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to resolve a tenant database for {TenantId}", group.Key);
            }
        }

        foreach (var group in reassignedGroups)
        {
            if (dict.TryGetValue(group.Key, out var command))
            {
                command.AddReassigns(group);
            }
            else
            {
                try
                {
                    var database = await GetDatabaseAsync(group.Key);
                    command = new DiscardAndReassignOutgoingAsyncGroup(database, nodeId);
                    dict[group.Key] = command;

                    command.AddReassigns(group);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error trying to resolve a tenant database for {TenantId}", group.Key);
                }
            }
        }

        foreach (var value in dict.Values) await _retryBlock.PostAsync(value);
    }

    public async Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming)
    {
        string tenantId = null!;
        try
        {
            tenantId = incoming.Select(x => x.TenantId).Distinct().Single()!;
        }
        catch (Exception)
        {
            throw new ArgumentOutOfRangeException(nameof(incoming),
                "Invalid in this case to use a mixed bag of tenanted envelopes");
        }

        var database = await GetDatabaseAsync(tenantId);
        await database.ReassignIncomingAsync(ownerId, incoming);
    }

    public string Name { get; } = null!;
    public void PromoteToMain(IWolverineRuntime runtime)
    {
        // Nothing here. 
    }

    public async Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit)
    {
        // Really just here for diagnostics
        var list = new List<Envelope>();
        foreach (var database in databases())
            list.AddRange(await database.LoadPageOfGloballyOwnedIncomingAsync(listenerAddress, limit));

        return list;
    }

    public async ValueTask DisposeAsync()
    {
        if (HasDisposed)
        {
            return;
        }

        foreach (var database in databases())
        {
            try
            {
                await database.DisposeAsync();
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception)
            {
            }
        }

        HasDisposed = true;
    }

    public void Initialize(IWolverineRuntime runtime)
    {
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        InitializeAsync(runtime).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
    }

    public bool HasDisposed { get; private set; }
    public IMessageInbox Inbox => this;
    public IMessageOutbox Outbox => this;
    public IDeadLetters DeadLetters => this;
    public IScheduledMessages ScheduledMessages => Main.ScheduledMessages;
    public INodeAgentPersistence Nodes => this;

    // Multi-tenant store delegates dynamic-listener registration to the main
    // store. Listener URIs aren't tenant-scoped - registering the same URI
    // across tenants would create duplicate listeners - so the master is
    // authoritative for the registry.
    public IListenerStore Listeners => Main.Listeners;

    // Recurring-message tracking is cluster-level bookkeeping owned by the single recurring
    // agent, which publishes through the main store — same reasoning as the listener registry
    // above: the master is authoritative, per-tenant rows would be duplicates.
    public IRecurringMessageStore RecurringMessages => Main.RecurringMessages;

    public IMessageStoreAdmin Admin => this;

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DatabaseDescriptor(subject) reads subject's runtime-type properties for diagnostic reporting. Trimmed-away properties on MultiTenantedMessageStore are silently omitted, which is acceptable for this diagnostic surface.")]
    public DatabaseDescriptor Describe()
    {
        return new DatabaseDescriptor(this)
        {
            DatabaseName = "nullo", Engine = "nullo"
        };
    }

    public Task DrainAsync()
    {
        return executeOnAllAsync(d => d.DrainAsync());
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        // TODO -- need to start ancillary stores too.
        // and probably refresh all
        return new CompositeAgent(new Uri("internal://scheduledjobs"),
            Source.AllActive().Select(x => x.StartScheduledJobs(runtime)));
    }

    Task IMessageStoreAdmin.DeleteAllHandledAsync()
    {
        return executeOnAllAsync(d => d.Admin.DeleteAllHandledAsync());
    }

    Task IMessageStoreAdmin.ClearAllAsync()
    {
        return executeOnAllAsync(d => d.Admin.ClearAllAsync());
    }

    Task IMessageStoreAdmin.RebuildAsync()
    {
        return executeOnAllAsync(d => d.Admin.RebuildAsync());
    }

    async Task<PersistedCounts> IMessageStoreAdmin.FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        foreach (var database in databases())
        {
            var db = await database.Admin.FetchCountsAsync();
            counts.Tenants[database.Name] = db;
            counts.Add(db);
        }

        return counts;
    }

    async Task<IReadOnlyList<Envelope>> IMessageStoreAdmin.AllIncomingAsync()
    {
        var list = new List<Envelope>();

        foreach (var database in databases())
        {
            var envelopes = await database.Admin.AllIncomingAsync();
            list.AddRange(envelopes);
        }

        return list;
    }

    async Task<IReadOnlyList<Envelope>> IMessageStoreAdmin.AllOutgoingAsync()
    {
        var list = new List<Envelope>();

        foreach (var database in databases())
        {
            var envelopes = await database.Admin.AllOutgoingAsync();
            list.AddRange(envelopes);
        }

        return list;
    }

    Task IMessageStoreAdmin.ReleaseAllOwnershipAsync()
    {
        return executeOnAllAsync(d => d.Admin.ReleaseAllOwnershipAsync());
    }

    Task IMessageStoreAdmin.ReleaseAllOwnershipAsync(int ownerId)
    {
        return executeOnAllAsync(async d =>
        {
            try
            {
                await d.Admin.ReleaseAllOwnershipAsync(ownerId);
            }
            catch (ObjectDisposedException)
            {
                // Can happen when the host is disposed without going through a clean
                // StopAsync()
            }
        });
    }

    Task IMessageStoreAdmin.CheckConnectivityAsync(CancellationToken token)
    {
        return executeOnAllAsync(d => d.Admin.CheckConnectivityAsync(token));
    }

    Task IMessageStoreAdmin.AssertStorageExistsAsync(CancellationToken token)
    {
        return executeOnAllAsync(d => d.Admin.AssertStorageExistsAsync(token));
    }

    Task IMessageStoreAdmin.AssertStorageProvisionedAsync(CancellationToken token)
    {
        return executeOnAllAsync(d => d.Admin.AssertStorageProvisionedAsync(token));
    }

    Task IMessageStoreAdmin.MigrateAsync()
    {
        return migrateAsync(null);
    }

    Task IMessageStoreAdmin.MigrateAsync(AutoCreate? overrideAutoCreate)
    {
        return migrateAsync(overrideAutoCreate);
    }

    private async Task migrateAsync(AutoCreate? overrideAutoCreate)
    {
        if (!_initialized)
        {
            await InitializeAsync(_runtime);
        }

        await Main.Admin.MigrateAsync(overrideAutoCreate);

        var exceptions = new List<Exception>();

        foreach (var assignment in Source.AllActiveByTenant())
        {
            try
            {
                await assignment.Value.Admin.MigrateAsync(overrideAutoCreate);
                _byTenant = _byTenant.AddOrUpdate(assignment.TenantId, assignment.Value);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Count != 0)
        {
            throw new AggregateException(exceptions);
        }
    }

    Task INodeAgentPersistence.ClearAllAsync(CancellationToken cancellationToken)
    {
        return Main.Nodes.ClearAllAsync(cancellationToken);
    }

    Task<int> INodeAgentPersistence.PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        return Main.Nodes.PersistAsync(node, cancellationToken);
    }

    async Task INodeAgentPersistence.DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        await Main.Nodes.DeleteAsync(nodeId, assignedNodeNumber);
        await executeOnAllAsync(async store => { await store.Admin.ReleaseAllOwnershipAsync(assignedNodeNumber); });
    }

    Task<IReadOnlyList<WolverineNode>> INodeAgentPersistence.LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        return Main.Nodes.LoadAllNodesAsync(cancellationToken);
    }

    Task INodeAgentPersistence.PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions, CancellationToken cancellationToken)
    {
        return Main.Nodes.PersistAgentRestrictionsAsync(restrictions, cancellationToken);
    }

    Task<NodeAgentState> INodeAgentPersistence.LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        return Main.Nodes.LoadNodeAgentStateAsync(cancellationToken);
    }

    Task INodeAgentPersistence.AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents,
        CancellationToken cancellationToken)
    {
        return Main.Nodes.AssignAgentsAsync(nodeId, agents, cancellationToken);
    }

    Task INodeAgentPersistence.RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        return Main.Nodes.RemoveAssignmentAsync(nodeId, agentUri, cancellationToken);
    }

    Task INodeAgentPersistence.AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        return Main.Nodes.AddAssignmentAsync(nodeId, agentUri, cancellationToken);
    }

    Task<bool> INodeAgentPersistence.TryClaimAssignmentAsync(Guid nodeId, Uri agentUri,
        CancellationToken cancellationToken)
    {
        return Main.Nodes.TryClaimAssignmentAsync(nodeId, agentUri, cancellationToken);
    }

    Task<WolverineNode?> INodeAgentPersistence.LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        return Main.Nodes.LoadNodeAsync(nodeId, cancellationToken);
    }

    Task<bool> INodeAgentPersistence.MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        return Main.Nodes.MarkHealthCheckAsync(node, cancellationToken);
    }

    Task INodeAgentPersistence.ReregisterNodeAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        return Main.Nodes.ReregisterNodeAsync(node, cancellationToken);
    }

    Task INodeAgentPersistence.OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
    {
        return Main.Nodes.OverwriteHealthCheckTimeAsync(nodeId, lastHeartbeatTime);
    }

    Task INodeAgentPersistence.LogRecordsAsync(params NodeRecord[] records)
    {
        return Main.Nodes.LogRecordsAsync(records);
    }

    Task<IReadOnlyList<NodeRecord>> INodeAgentPersistence.FetchRecentRecordsAsync(int count)
    {
        return Main.Nodes.FetchRecentRecordsAsync(count);
    }

    // GH-3701: node records are written to, and read back from, the Main store only -- see LogRecordsAsync
    // and FetchRecentRecordsAsync above -- so the retention cap has to follow them there. Without this
    // override a multi-tenanted store inherited the interface's no-op default and never trimmed at all,
    // which is exactly the shape (one main store, hundreds of tenant databases) the 36M-row report came from.
    Task INodeAgentPersistence.DeleteOldNodeRecordsAsync(int retainCount)
    {
        return Main.Nodes.DeleteOldNodeRecordsAsync(retainCount);
    }

    bool INodeAgentPersistence.HasLeadershipLock()
    {
        return Main.Nodes.HasLeadershipLock();
    }

    Task<bool> INodeAgentPersistence.TryAttainLeadershipLockAsync(CancellationToken token)
    {
        return Main.Nodes.TryAttainLeadershipLockAsync(token);
    }

    Task INodeAgentPersistence.ReleaseLeadershipLockAsync()
    {
        return Main.Nodes.ReleaseLeadershipLockAsync();
    }

    public async ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(MessageContext context)
        where TSaga : Saga
    {
        if (context.IsDefaultTenant())
        {
            if (Main is ISagaSupport s1)
            {
                return await s1.EnrollAndFetchSagaStorage<TId, TSaga>(context);
            }
        }

        var store = await Source.FindAsync(context.TenantId!) as ISagaSupport;
        if (store != null)
        {
            return await store.EnrollAndFetchSagaStorage<TId, TSaga>(context);
        }

        // GH-4531: the remedy here is different from the single-store case -- the saga either moves off
        // the tenant store, or the tenant store has to be one that supports sagas.
        throw new InvalidOperationException(
            $"The tenant store for tenant '{context.TenantId}' does not implement {typeof(ISagaSupport).FullNameInCode()} and cannot be used for saga persistence. " +
            "Saga state is stored by the message store: PersistMessagesWithPostgresql/SqlServer/MySql/Sqlite/Oracle (lightweight saga tables), " +
            "IntegrateWithWolverine() on a Marten/Polecat/Fisher store, an EF Core DbContext under UseEntityFrameworkCoreTransactions(), " +
            "or RavenDb/CosmosDb/Redis persistence. Register a tenant store that supports sagas, or move this saga off the tenant store.");
    }

    public async Task InitializeAsync(IWolverineRuntime runtime)
    {
        if (_initialized)
        {
            return;
        }

        await Source.RefreshAsync();

        foreach (var database in databases()) database.Initialize(runtime);

        _initialized = true;
    }

    public IReadOnlyList<IMessageStore> ActiveDatabases()
    {
        return databases().ToArray();
    }

    public async ValueTask<IMessageStore> GetDatabaseAsync(string? tenantId)
    {
        if (tenantId.IsDefaultTenant())
        {
            return Main;
        }

        if (tenantId!.EqualsIgnoreCase(TransportConstants.Default))
        {
            return Main;
        }

        if (tenantId!.EqualsIgnoreCase(StorageConstants.Main))
        {
            return Main;
        }

        if (_byTenant.TryFind(tenantId!, out var store))
        {
            return store;
        }

        store = await Source.FindAsync(tenantId!);
        if (store != null && _runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }

        _byTenant = _byTenant.AddOrUpdate(tenantId!, store!);

        return store!;
    }

    private IEnumerable<IMessageStore> databases()
    {
        yield return Main;

        foreach (var database in Source.AllActive()) yield return database;
    }

    private async Task executeOnAllAsync(Func<IMessageStore, Task> action)
    {
        var exceptions = new List<Exception>();

        foreach (var database in databases())
        {
            try
            {
                await action(database);
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Count != 0)
        {
            throw new AggregateException(exceptions);
        }
    }

    internal interface IEnvelopeCommand
    {
        Task ExecuteAsync(CancellationToken cancellationToken);
    }

    // GH-4435: the StoreIncomingAsyncGroup command that used to live here is gone. Posting the inbox
    // insert to the retry block is what swallowed the failure -- the block never rethrows, so the
    // receiver acked envelopes that had never been stored. StoreIncomingAsync now awaits each store
    // directly and reports what did not land.

    // GH-4435: DeleteOutgoingAsyncGroup is gone with the retry-block hop it existed for. _retryBlock and
    // IEnvelopeCommand remain in use by DiscardAndReassignOutgoingAsync below.

    internal class DiscardAndReassignOutgoingAsyncGroup : IEnvelopeCommand
    {
        private readonly List<Envelope> _discards = new();
        private readonly int _nodeId;
        private readonly List<Envelope> _reassigned = new();
        private readonly IMessageStore _store;

        public DiscardAndReassignOutgoingAsyncGroup(IMessageStore store, int nodeId)
        {
            _store = store;
            _nodeId = nodeId;
        }

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            return _store.Outbox.DiscardAndReassignOutgoingAsync(_discards.ToArray(), _reassigned.ToArray(), _nodeId);
        }

        public void AddDiscards(IEnumerable<Envelope> discards)
        {
            _discards.AddRange(discards);
        }

        public void AddReassigns(IEnumerable<Envelope> reassigns)
        {
            _reassigned.AddRange(reassigns);
        }
    }
}