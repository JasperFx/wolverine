using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Wolverine.Util;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
///     GH-4634. The conjoined ("one database, a <c>tenant_id</c> column") multi-tenancy battery, written
///     once and run against every document store that supports conjoined documents: Marten, Polecat and
///     Fisher. Marten had this behaviour covered in pieces across the tenant-partitioned aggregate matrix,
///     the HTTP detection suite and the Rabbit saga-timeout test; Polecat and Fisher had <b>nothing</b>,
///     while sharing the same <c>OutboxedSessionFactory.OpenSession(context, tenantId)</c> shape.
/// </summary>
/// <remarks>
///     <para>
///         Every store-specific thing a test here needs is an abstract member, so the battery itself never
///         names a store: <see cref="configureStore" /> for registration,
///         <see cref="LoadTodoAsync" /> / <see cref="StoreTodoAsync" /> for a tenant-scoped document read
///         and write, and <see cref="LoadTallyAsync" /> for a tenant-scoped live aggregation.
///     </para>
///     <para>
///         <b>The host is deliberately on <see cref="TenantIdStyle.ForceLowerCase" />.</b> Every tenant id
///         the battery uses is already lower case, so it changes nothing for items 1-6 and 8 — but it is
///         the one setting that makes item 7 mean anything. It is what exposed GH-4640: the envelope kept
///         the raw casing while the context was normalised, so Polecat and Fisher, which build their
///         session from <c>Envelope.TenantId</c>, wrote the wrong <c>tenant_id</c>. Fixed in
///         <c>MessageContext.ReadEnvelope</c>; item 7 now guards it on all three stores. A fixture whose
///         store also has the setting (Marten does; Polecat and Fisher do not) should set it there too.
///     </para>
///     <para>
///         <b>On skipping.</b> One item genuinely does not apply everywhere — the default tenant cannot be
///         disabled on a store that has no such switch. That is expressed as an overridable skip reason
///         rather than an absent test, so the runner reports the gap out loud instead of a fixture quietly
///         running one fewer test than its siblings.
///     </para>
/// </remarks>
public abstract class ConjoinedTenancyCompliance : IAsyncLifetime
{
    protected IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await startHostAsync(true);
        await initializeStoreAsync(theHost);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();

        await disposeStoreAsync();
    }

    /// <summary>Tear down anything the fixture created outside the host -- a SQLite file, say.</summary>
    protected virtual Task disposeStoreAsync() => Task.CompletedTask;

    protected async Task<IHost> startHostAsync(bool defaultTenantUsageEnabled)
    {
        var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(TenantedTodoHandler))
                    .IncludeType(typeof(TenantedCascadeHandler))
                    .IncludeType(typeof(TenantedTallyHandler))
                    .IncludeType(typeof(TenantedComplianceSaga));

                // One file, one node for Fisher; harmless and faster for the other two.
                opts.Durability.Mode = DurabilityMode.Solo;

                // See the class remarks -- this is what makes item 7 observable.
                opts.Durability.TenantIdStyle = TenantIdStyle.ForceLowerCase;

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddSingleton<TenantedTracker>();

                configureStore(opts, defaultTenantUsageEnabled);
            }).StartAsync();

        return host;
    }

    /// <summary>
    ///     Register the store, conjoined, and integrate it with Wolverine. When
    ///     <paramref name="defaultTenantUsageEnabled" /> is false the store must refuse a session opened
    ///     with no tenant; a store with no such switch should leave it alone and override
    ///     <see cref="defaultTenantDisabledSkipReason" />.
    /// </summary>
    protected abstract void configureStore(WolverineOptions opts, bool defaultTenantUsageEnabled);

    /// <summary>Apply schema changes, clean leftover documents -- whatever the store needs before a test.</summary>
    protected virtual Task initializeStoreAsync(IHost host) => Task.CompletedTask;

    /// <summary>Read one document through a session scoped to <paramref name="tenantId" />.</summary>
    protected abstract Task<TenantedTodo?> LoadTodoAsync(IHost host, string tenantId, Guid id);

    /// <summary>Write one document through a session scoped to <paramref name="tenantId" />, outside Wolverine.</summary>
    protected abstract Task StoreTodoAsync(IHost host, string tenantId, TenantedTodo todo);

    /// <summary>Live-aggregate one event stream through a session scoped to <paramref name="tenantId" />.</summary>
    protected abstract Task<TenantTally?> LoadTallyAsync(IHost host, string tenantId, Guid id);

    /// <summary>
    ///     The value actually in the document row's <c>tenant_id</c> column, read through a session scoped
    ///     to <paramref name="tenantId" />. All three stores expose this as <c>MetadataForAsync</c>.
    /// </summary>
    protected abstract Task<string?> StoredTenantIdAsync(IHost host, string tenantId, Guid id);

    /// <summary>
    ///     Non-null when this store cannot disable default-tenant usage. The string is used as the skip
    ///     reason, so it has to name the constraint rather than say "not supported".
    /// </summary>
    protected virtual string? defaultTenantDisabledSkipReason => null;

    /// <summary>The exception the store raises when a session is opened with no tenant and the default tenant is off.</summary>
    protected virtual Type defaultTenantUsageDisabledExceptionType =>
        throw new NotSupportedException(
            $"{GetType().Name} must either override {nameof(defaultTenantUsageDisabledExceptionType)} " +
            $"or override {nameof(defaultTenantDisabledSkipReason)}");

    protected TenantedTracker theTracker => theHost.Services.GetRequiredService<TenantedTracker>();

    // ----------------------------------------------------------------------------------------------
    // Item 1 -- InvokeForTenantAsync, [Entity], aggregate isolation, required-missing
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task invoke_for_tenant_lands_the_write_in_that_tenant()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new CreateTenantedTodo(id, "Andor"), "one");

        (await LoadTodoAsync(theHost, "one", id))!.Name.ShouldBe("Andor");
        (await LoadTodoAsync(theHost, "two", id)).ShouldBeNull();
    }

    [Fact]
    public async Task the_same_id_in_two_tenants_is_two_documents()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new CreateTenantedTodo(id, "Andor"), "one");
        await theHost.InvokeMessageAndWaitAsync(new CreateTenantedTodo(id, "Tear"), "two");

        (await LoadTodoAsync(theHost, "one", id))!.Name.ShouldBe("Andor");
        (await LoadTodoAsync(theHost, "two", id))!.Name.ShouldBe("Tear");
    }

    [Fact]
    public async Task entity_attribute_loads_and_writes_inside_the_tenant()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new CreateTenantedTodo(id, "Andor"), "one");

        // Seeded out of band rather than through a handler, so the "other tenant untouched" assertion
        // below does not depend on the same write path it is checking.
        await StoreTodoAsync(theHost, "two", new TenantedTodo { Id = id, Name = "Tear" });

        await theHost.InvokeMessageAndWaitAsync(new RenameTenantedTodo(id, "Caemlyn"), "one");

        (await LoadTodoAsync(theHost, "one", id))!.Name.ShouldBe("Caemlyn");

        // The other tenant's document with the same id is untouched -- which is the half that would
        // still be green if [Entity] resolved against a session with no tenant at all.
        (await LoadTodoAsync(theHost, "two", id))!.Name.ShouldBe("Tear");
    }

    [Fact]
    public async Task required_entity_missing_in_the_routed_tenant_throws()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new CreateTenantedTodo(id, "Andor"), "one");

        // Raw InvokeForTenantAsync rather than a tracked session: tracking would wrap the exception.
        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            theHost.MessageBus().InvokeForTenantAsync("two", new RenameTenantedTodo(id, "Caemlyn")));
    }

    [Fact]
    public async Task write_aggregate_appends_inside_the_tenant_and_stays_isolated()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new AppendTally(id, 4), "one");

        (await LoadTallyAsync(theHost, "one", id))!.Total.ShouldBe(4);
        (await LoadTallyAsync(theHost, "two", id)).ShouldBeNull();
    }

    [Fact]
    public async Task the_same_stream_id_in_two_tenants_stays_independent()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new AppendTally(id, 4), "one");
        await theHost.InvokeMessageAndWaitAsync(new AppendTally(id, 9), "two");

        (await LoadTallyAsync(theHost, "one", id))!.Total.ShouldBe(4);
        (await LoadTallyAsync(theHost, "two", id))!.Total.ShouldBe(9);
    }

    [Fact]
    public async Task read_aggregate_reads_the_routed_tenant()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new AppendTally(id, 7), "one");

        (await theHost.MessageBus().InvokeForTenantAsync<TallyView>("one", new ViewTally(id))).Total.ShouldBe(7);

        // -1 is the handler's "no aggregate here" sentinel
        (await theHost.MessageBus().InvokeForTenantAsync<TallyView>("two", new ViewTally(id))).Total.ShouldBe(-1);
    }

    [Fact]
    public async Task required_write_aggregate_missing_in_the_routed_tenant_throws()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new AppendTally(id, 4), "one");

        await Should.ThrowAsync<RequiredDataMissingException>(() =>
            theHost.MessageBus().InvokeForTenantAsync("two", new RequireAppendTally(id, 3)));

        (await LoadTallyAsync(theHost, "two", id)).ShouldBeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // Item 2 -- a cascaded message's WRITE lands in the originating tenant
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task a_cascaded_handler_writes_into_the_originating_tenant()
    {
        // The existing Marten cascade test records envelope.TenantId in the downstream handler and stops
        // there -- it proves the tenant rode on the envelope, not that the downstream handler's session
        // was scoped to it. This one makes the downstream handler persist.
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new StartTenantedCascade(id, "Andor"), "one");

        (await LoadTodoAsync(theHost, "one", id))!.Name.ShouldBe("Andor/cascaded");
        (await LoadTodoAsync(theHost, "two", id)).ShouldBeNull();

        theTracker.TenantFor(id).ShouldBe("one");
    }

    // ----------------------------------------------------------------------------------------------
    // Item 3 -- DeliveryOptions.TenantId routing, and AutoApplyTransactions under conjoined tenancy
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task delivery_options_tenant_id_routes_the_write()
    {
        var id = Guid.NewGuid();

        await theHost.TrackActivity().ExecuteAndWaitAsync(c =>
            c.PublishAsync(new CreateTenantedTodo(id, "Illian"), new DeliveryOptions { TenantId = "three" }));

        (await LoadTodoAsync(theHost, "three", id))!.Name.ShouldBe("Illian");
        (await LoadTodoAsync(theHost, "one", id)).ShouldBeNull();
    }

    [Fact]
    public async Task auto_apply_transactions_rolls_the_tenant_write_back_on_failure()
    {
        // AutoApplyTransactions is what commits every other test in this class; the only way to show it is
        // a real transaction rather than a SaveChangesAsync that happens to run is to make the handler
        // throw after the write is queued.
        var id = Guid.NewGuid();

        await Should.ThrowAsync<DeliberateTenancyFailure>(() =>
            theHost.MessageBus().InvokeForTenantAsync("one", new CreateTenantedTodoThenFail(id, "Ghealdan")));

        (await LoadTodoAsync(theHost, "one", id)).ShouldBeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // Item 4 -- sagas
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task a_saga_correlated_under_another_tenant_is_not_found()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new StartTenantedSaga(id), "one");
        await theHost.InvokeMessageAndWaitAsync(new ContinueTenantedSaga(id), "one");

        theTracker.SagaHandled(id).ShouldBe(1);
        theTracker.SagaNotFound(id).ShouldBe(0);

        // Same saga id, different tenant: the lookup has to miss.
        await theHost.InvokeMessageAndWaitAsync(new ContinueTenantedSaga(id), "two");

        theTracker.SagaHandled(id).ShouldBe(1);
        theTracker.SagaNotFound(id).ShouldBe(1);
    }

    [Fact]
    public async Task the_same_saga_id_in_two_tenants_stays_independent()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new StartTenantedSaga(id), "one");
        await theHost.InvokeMessageAndWaitAsync(new StartTenantedSaga(id), "two");

        await theHost.InvokeMessageAndWaitAsync(new ContinueTenantedSaga(id), "one");
        await theHost.InvokeMessageAndWaitAsync(new ContinueTenantedSaga(id), "one");
        await theHost.InvokeMessageAndWaitAsync(new ContinueTenantedSaga(id), "two");

        theTracker.SagaNotFound(id).ShouldBe(0);

        await theHost.InvokeMessageAndWaitAsync(new CompleteTenantedSaga(id), "one");
        await theHost.InvokeMessageAndWaitAsync(new CompleteTenantedSaga(id), "two");

        theTracker.SagaCount(id, "one").ShouldBe(2);
        theTracker.SagaCount(id, "two").ShouldBe(1);
    }

    [Fact]
    public async Task a_scheduled_saga_timeout_keeps_the_tenant()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new StartTimedOutSaga(id), "one");

        var captured = await theTracker.WaitForTimeoutAsync(id, 30.Seconds());

        captured.EnvelopeTenantId.ShouldBe("one");
        captured.ContextTenantId.ShouldBe("one");
        captured.StoredTenantId.ShouldBe("one");
    }

    // ----------------------------------------------------------------------------------------------
    // Item 6 -- default tenant usage disabled
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task a_handler_invoked_with_no_tenant_throws_when_the_default_tenant_is_disabled()
    {
        if (defaultTenantDisabledSkipReason is { } skip) Assert.Skip(skip);

        var host = await startHostAsync(false);
        try
        {
            await initializeStoreAsync(host);

            var ex = await Should.ThrowAsync<Exception>(() =>
                host.MessageBus().InvokeAsync(new CreateTenantedTodo(Guid.NewGuid(), "Nowhere")));

            ex.ShouldBeOfType(defaultTenantUsageDisabledExceptionType);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ----------------------------------------------------------------------------------------------
    // Item 7 -- TenantIdStyle.ForceLowerCase in front of a conjoined store
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_envelope_tenant_id_is_normalised_like_the_context()
    {
        // GH-4640, asserted where it actually lives and with no store in the picture.
        // InvokeForTenantAsync puts the raw string on DeliveryOptions (MessageBus.cs:178) and Executor
        // copies it straight onto the envelope, so the two used to disagree: the context normalised
        // through TenantIdStyle and the envelope kept "MIXED". MessageContext.ReadEnvelope now writes
        // the normalised value back onto the envelope, which every execution path passes through.
        var id = Guid.NewGuid();

        await theHost.MessageBus().InvokeForTenantAsync("MIXED", new RecordTenantIds(id));

        var seen = theTracker.TenantIdsFor(id);
        seen.ContextTenantId.ShouldBe("mixed");
        seen.EnvelopeTenantId.ShouldBe("mixed");
    }

    [Fact]
    public async Task force_lower_case_reaches_the_stored_tenant_id()
    {
        var id = Guid.NewGuid();

        // InvokeForTenantAsync and NOT InvokeMessageAndWaitAsync(message, tenantId): the tracking
        // overload assigns MessageContext.TenantId, whose setter normalises, so the raw spelling never
        // reaches the envelope and the whole test goes vacuously green. InvokeForTenantAsync puts the
        // string on DeliveryOptions instead, which is the path this item is about.
        await theHost.MessageBus().InvokeForTenantAsync("RED", new CreateTenantedTodo(id, "Andor"));

        // Both a document session (the write) and a query session (the read) have to land on "red".
        (await LoadTodoAsync(theHost, "red", id))!.Name.ShouldBe("Andor");

        // ...and the row itself has to say "red". Retrievability alone is not enough: on a SQL Server
        // database with the usual case-insensitive collation, `tenant_id = 'red'` happily matches a row
        // stored as 'RED', so the read above passes over a value Wolverine believes it normalised.
        (await StoredTenantIdAsync(theHost, "red", id)).ShouldBe("red");
    }

    // ----------------------------------------------------------------------------------------------
    // Item 8 -- local durable scheduling keeps the tenant
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task a_locally_scheduled_message_keeps_the_tenant()
    {
        var id = Guid.NewGuid();

        // A tracked session rather than the tracker's own signal: the tracker fires inside the handler,
        // while AutoApplyTransactions commits in a postprocessor AFTER it, so reading the document off
        // the tracker's signal races the commit. The tracked session's Executed event is raised once the
        // whole chain, postprocessors included, has run.
        await theHost.TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<DelayedTenantedTodo>(theHost)
            .ExecuteAndWaitAsync(c =>
                c.ScheduleAsync(new DelayedTenantedTodo(id, "Cairhien"), 1.Seconds(),
                    new DeliveryOptions { TenantId = "one" }));

        (await LoadTodoAsync(theHost, "one", id))!.Name.ShouldBe("Cairhien");
        (await LoadTodoAsync(theHost, "two", id)).ShouldBeNull();
    }

    [Fact]
    public async Task a_replayed_dead_letter_keeps_the_tenant()
    {
        var id = Guid.NewGuid();
        theTracker.FailNextDeadLetter = true;

        await theHost.TrackActivity()
            .Timeout(30.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c =>
                c.SendAsync(new DeadLetteredTenantedTodo(id, "Tarabon"),
                    new DeliveryOptions { TenantId = "one" }).AsTask());

        var store = theHost.Services.GetRequiredService<IMessageStore>();
        var deadLetter = await waitForDeadLetterAsync(store);

        // The tenant has to survive the trip into dead-letter storage before it can survive the trip back.
        deadLetter.Envelope.TenantId.ShouldBe("one");

        theTracker.FailNextDeadLetter = false;

        await theHost.TrackActivity()
            .Timeout(30.Seconds())
            .DoNotAssertOnExceptionsDetected()
            .WaitForMessageToBeReceivedAt<DeadLetteredTenantedTodo>(theHost)
            .ExecuteAndWaitAsync((IMessageContext _) =>
                store.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync([deadLetter.Id]));

        (await LoadTodoAsync(theHost, "one", id))!.Name.ShouldBe("Tarabon");
        (await LoadTodoAsync(theHost, "two", id)).ShouldBeNull();
    }

    private static async Task<DeadLetterEnvelope> waitForDeadLetterAsync(IMessageStore store)
    {
        var query = new DeadLetterEnvelopeQuery
        {
            PageSize = 25,
            MessageType = typeof(DeadLetteredTenantedTodo).ToMessageTypeName()
        };

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < 30.Seconds())
        {
            var page = await store.DeadLetters.QueryAsync(query, CancellationToken.None);
            if (page.Envelopes.Count > 0)
            {
                return page.Envelopes[0];
            }

            await Task.Delay(100.Milliseconds());
        }

        throw new ShouldAssertException(
            "The failing message never reached dead-letter storage within 30s, so there was nothing to " +
            "replay. Check that the local queue is durable on this fixture.");
    }
}

#region model

public class TenantedTodo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class TenantTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public void Apply(TallyIncremented e) => Total += e.Amount;
}

public record TallyIncremented(int Amount);

public record CreateTenantedTodo(Guid Id, string Name);

public record CreateTenantedTodoThenFail(Guid Id, string Name);

public record RenameTenantedTodo(Guid Id, string Name);

public record StartTenantedCascade(Guid Id, string Name);

public record FinishTenantedCascade(Guid Id, string Name);

public record DelayedTenantedTodo(Guid Id, string Name);

public record DeadLetteredTenantedTodo(Guid Id, string Name);

public record RecordTenantIds(Guid Id);

public record AppendTally(Guid TenantTallyId, int Amount);

public record RequireAppendTally(Guid TenantTallyId, int Amount);

public record ViewTally(Guid TenantTallyId);

public record TallyView(int Total);

public record StartTenantedSaga(Guid Id);

public record ContinueTenantedSaga(Guid Id);

public record CompleteTenantedSaga(Guid Id);

public record StartTimedOutSaga(Guid Id);

public record TenantedSagaTimeout(Guid Id) : TimeoutMessage(2.Seconds());

public class DeliberateTenancyFailure : Exception
{
    public DeliberateTenancyFailure() : base("Deliberate failure to prove the tenant write rolls back")
    {
    }
}

public static class TenantedTodoHandler
{
    public static IStorageAction<TenantedTodo> Handle(CreateTenantedTodo command)
        => Storage.Insert(new TenantedTodo { Id = command.Id, Name = command.Name });

    public static IStorageAction<TenantedTodo> Handle(CreateTenantedTodoThenFail command)
        => throw new DeliberateTenancyFailure();

    public static IStorageAction<TenantedTodo> Handle(RenameTenantedTodo command,
        [Entity(OnMissing = OnMissing.ThrowException)] TenantedTodo todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }

    public static IStorageAction<TenantedTodo> Handle(DelayedTenantedTodo command)
        => Storage.Insert(new TenantedTodo { Id = command.Id, Name = command.Name });

    public static IStorageAction<TenantedTodo> Handle(DeadLetteredTenantedTodo command,
        TenantedTracker tracker)
    {
        if (tracker.FailNextDeadLetter)
        {
            throw new DeliberateTenancyFailure();
        }

        return Storage.Insert(new TenantedTodo { Id = command.Id, Name = command.Name });
    }

    public static void Handle(RecordTenantIds command, Envelope envelope, IMessageContext context,
        TenantedTracker tracker)
    {
        tracker.RecordTenantIds(command.Id, envelope.TenantId, context.TenantId);
    }
}

public static class TenantedCascadeHandler
{
    public static (IStorageAction<TenantedTodo>, OutgoingMessages) Handle(StartTenantedCascade command)
    {
        return (Storage.Nothing<TenantedTodo>(),
            new OutgoingMessages { new FinishTenantedCascade(command.Id, command.Name) });
    }

    public static IStorageAction<TenantedTodo> Handle(FinishTenantedCascade command, Envelope envelope,
        TenantedTracker tracker)
    {
        tracker.RecordCascade(command.Id, envelope.TenantId);
        return Storage.Insert(new TenantedTodo { Id = command.Id, Name = command.Name + "/cascaded" });
    }
}

public static class TenantedTallyHandler
{
    public static EventsToAppend Handle(AppendTally command,
        [WriteModel(Required = false)] TenantTally? tally)
        => new() { new TallyIncremented(command.Amount) };

    public static EventsToAppend Handle(RequireAppendTally command,
        [WriteModel(Required = true, OnMissing = OnMissing.ThrowException)]
        TenantTally tally)
        => new() { new TallyIncremented(command.Amount) };

    public static TallyView Handle(ViewTally command, [ReadModel(Required = false)] TenantTally? tally)
        => new(tally?.Total ?? -1);
}

public class TenantedComplianceSaga : Saga
{
    public Guid Id { get; set; }
    public int Count { get; set; }
    public string? StoredTenantId { get; set; }

    public static TenantedComplianceSaga Start(StartTenantedSaga command, Envelope envelope)
        => new() { Id = command.Id, StoredTenantId = envelope.TenantId };

    public static (TenantedComplianceSaga, TenantedSagaTimeout) Start(StartTimedOutSaga command,
        Envelope envelope)
        => (new TenantedComplianceSaga { Id = command.Id, StoredTenantId = envelope.TenantId },
            new TenantedSagaTimeout(command.Id));

    public void Handle(ContinueTenantedSaga command, TenantedTracker tracker)
    {
        Count++;
        tracker.SagaWasHandled(command.Id);
    }

    public static void NotFound(ContinueTenantedSaga command, TenantedTracker tracker)
        => tracker.SagaWasNotFound(command.Id);

    public void Handle(CompleteTenantedSaga command, Envelope envelope, TenantedTracker tracker)
    {
        tracker.RecordSagaCount(command.Id, envelope.TenantId, Count);
        MarkCompleted();
    }

    public void Handle(TenantedSagaTimeout timeout, Envelope envelope, IMessageContext context,
        TenantedTracker tracker)
    {
        tracker.RecordTimeout(timeout.Id, envelope.TenantId, context.TenantId, StoredTenantId);
        MarkCompleted();
    }
}

/// <summary>
///     Registered as a singleton on each host rather than being static, so a second host built inside a
///     test (the default-tenant-disabled case) cannot see the first host's records.
/// </summary>
public class TenantedTracker
{
    public record TenantIdPair(string? EnvelopeTenantId, string? ContextTenantId);

    public record TimeoutSnapshot(string? EnvelopeTenantId, string? ContextTenantId, string? StoredTenantId);

    public volatile bool FailNextDeadLetter;

    private readonly ConcurrentDictionary<Guid, TenantIdPair> _tenantIds = new();
    private readonly ConcurrentDictionary<Guid, string?> _cascades = new();
    private readonly ConcurrentDictionary<Guid, int> _sagaHandled = new();
    private readonly ConcurrentDictionary<Guid, int> _sagaNotFound = new();
    private readonly ConcurrentDictionary<(Guid, string?), int> _sagaCounts = new();

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<TimeoutSnapshot>> _timeouts = new();

    public void RecordTenantIds(Guid id, string? envelopeTenantId, string? contextTenantId)
        => _tenantIds[id] = new TenantIdPair(envelopeTenantId, contextTenantId);

    public TenantIdPair TenantIdsFor(Guid id)
    {
        _tenantIds.TryGetValue(id, out var pair).ShouldBeTrue($"No tenant ids were recorded for {id}");
        return pair!;
    }

    public void RecordCascade(Guid id, string? tenantId) => _cascades[id] = tenantId;

    public string? TenantFor(Guid id)
    {
        _cascades.TryGetValue(id, out var tenantId)
            .ShouldBeTrue($"The cascaded handler never ran for {id}");
        return tenantId;
    }

    public void SagaWasHandled(Guid id) => _sagaHandled.AddOrUpdate(id, 1, (_, current) => current + 1);

    public int SagaHandled(Guid id) => _sagaHandled.TryGetValue(id, out var count) ? count : 0;

    public void SagaWasNotFound(Guid id) => _sagaNotFound.AddOrUpdate(id, 1, (_, current) => current + 1);

    public int SagaNotFound(Guid id) => _sagaNotFound.TryGetValue(id, out var count) ? count : 0;

    public void RecordSagaCount(Guid id, string? tenantId, int count) => _sagaCounts[(id, tenantId)] = count;

    public int SagaCount(Guid id, string? tenantId)
    {
        _sagaCounts.TryGetValue((id, tenantId), out var count)
            .ShouldBeTrue($"The saga for {id} in tenant '{tenantId}' never completed");
        return count;
    }

    public void RecordTimeout(Guid id, string? envelopeTenantId, string? contextTenantId, string? storedTenantId)
        => timeoutSource(id).TrySetResult(new TimeoutSnapshot(envelopeTenantId, contextTenantId, storedTenantId));

    public async Task<TimeoutSnapshot> WaitForTimeoutAsync(Guid id, TimeSpan timeout)
    {
        try
        {
            return await timeoutSource(id).Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new ShouldAssertException(
                $"The scheduled saga timeout for {id} never reached the saga handler within {timeout}. " +
                "The likeliest cause is that the timeout envelope's tenant id did not survive the durable " +
                "scheduling round trip, so the saga lookup missed and the message was silently discarded.");
        }
    }

    private TaskCompletionSource<TimeoutSnapshot> timeoutSource(Guid id)
        => _timeouts.GetOrAdd(id, _ => new TaskCompletionSource<TimeoutSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously));
}

#endregion
