using System.Text.Json;
using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Runtime;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

// GH-5415: vocabulary for specifying event-sourced sagas -- their snapshot, completion, retention and history -- and a
// service that refuses to start. Written with the specifications in src/Samples/EventSourcedSagas, wording awaiting
// review in SPEC-REVIEW-5415.md.
public partial class WolverineScenario
{
    private SagaHistoryPage? _historyPage;
    private SagaHistoryPageRequest? _historyRequest;
    private SagaInstanceState? _sagaAt;
    private Type? _sagaAtType;
    private bool _serviceStartAttempted;
    private Exception? _serviceStartFailure;

    // ---- the saga's history (ISagaHistoryDiagnostics) -------------------------------------------------------------

    /// <summary>
    /// GH-5415. The act: page <paramref name="page" /> of the <typeparamref name="TSaga" /> saga's history is read
    /// through <see cref="ISagaHistoryDiagnostics.ReadSagaHistoryAsync" />, as CritterWatch reads it. Reads
    /// "page 1 of the OrderFulfillment saga #OrderFulfillment's history is read, 2 events to a page".
    /// </summary>
    public Task WhenTheSagaHistoryIsRead<TSaga>(object sagaId, int page = 1, int pageSize = 50) where TSaga : Saga
    {
        var text = $"page {page} of the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)}'s history is read, " +
                   $"{pageSize} events to a page";

        root._historyRequest = new SagaHistoryPageRequest(page, pageSize);
        return root.readAct(text, typeof(TSaga), async history =>
        {
            root._historyPage = await history.ReadSagaHistoryAsync(typeof(TSaga).FullNameInCode(), sagaId,
                new SagaHistoryPageRequest(page, pageSize), CancellationToken.None);
        });
    }

    /// <summary>
    /// GH-5415. The act: the <typeparamref name="TSaga" /> saga is read as it was after the event at
    /// <paramref name="version" /> of its stream (<see cref="ISagaHistoryDiagnostics.ReadSagaAtAsync(string, object, long, CancellationToken)" />).
    /// Reads "the OrderFulfillment saga #OrderFulfillment is read as it was at version 2".
    /// </summary>
    public Task WhenTheSagaIsReadAtVersion<TSaga>(object sagaId, long version) where TSaga : Saga
    {
        var text = $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is read as it was at version {version}";
        return root.readAct(text, typeof(TSaga), async history =>
        {
            root._sagaAt = await history.ReadSagaAtAsync(typeof(TSaga).FullNameInCode(), sagaId, version, CancellationToken.None);
        }, pointInTime: true);
    }

    /// <summary>
    /// GH-5415. The act: the <typeparamref name="TSaga" /> saga is read as it was at <paramref name="timestamp" />
    /// (<see cref="ISagaHistoryDiagnostics.ReadSagaAtAsync(string, object, DateTimeOffset, CancellationToken)" />).
    /// <paramref name="described" /> says what the moment was, since a timestamp reads badly: "when it was paid".
    /// Reads "the OrderFulfillment saga #OrderFulfillment is read as it was when it was paid".
    /// </summary>
    public Task WhenTheSagaIsReadAsOf<TSaga>(object sagaId, DateTimeOffset timestamp, string described) where TSaga : Saga
    {
        var text = $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is read as it was {described}";
        return root.readAct(text, typeof(TSaga), async history =>
        {
            root._sagaAt = await history.ReadSagaAtAsync(typeof(TSaga).FullNameInCode(), sagaId, timestamp, CancellationToken.None);
        }, pointInTime: true);
    }

    /// <summary>
    /// GH-5415. The page the last <see cref="WhenTheSagaHistoryIsRead{TSaga}" /> read holds events of exactly these
    /// types, in this order, at consecutive versions. Reads "the history page holds OrderFulfillmentStarted, PaymentConfirmed".
    /// </summary>
    public void ThenTheSagaHistoryIs(params Type[] eventTypes)
    {
        using var step = ScenarioRecorder.Step("Then", $"the history page holds {string.Join(", ", eventTypes.Select(nameOf))}");
        if (historyPage() is not { } page) return;

        // GH-5665: an entry that is the expected type is named as the expected type is, which also covers a generic
        // message such as Fault<T>, whose full name does not shorten
        var actual = page.Events
            .Select((e, i) => i < eventTypes.Length && isEventOf(e, eventTypes[i]) ? nameOf(eventTypes[i]) : describeHistoryEvent(e))
            .ToArray();
        Verdicts.Check("events", string.Join(", ", actual), string.Join(", ", eventTypes.Select(nameOf)));
        Verdicts.Fact(actual.Length == eventTypes.Length &&
                      page.Events.Zip(eventTypes).All(x => isEventOf(x.First, x.Second)),
            $"Expected the history page to hold {string.Join(", ", eventTypes.Select(nameOf))}, but it holds " +
            (actual.Length == 0 ? "no events" : string.Join(", ", actual)) + ".");

        var versions = page.Events.Select(x => x.Version).ToArray();
        Verdicts.Fact(versions.Zip(versions.Skip(1)).All(x => x.Second == x.First + 1),
            $"Expected the history page's events at consecutive versions, but they are at {string.Join(", ", versions)}.");
    }

    /// <summary>
    /// GH-5415. The saga's whole history, every page together, has <paramref name="count" /> events, and whether there
    /// is a page after the one read follows from it. Reads "the whole history has 5 events, so there are more pages".
    /// </summary>
    public void ThenTheSagaHistoryHasInAll(long count)
    {
        var request = root._historyRequest;
        var more = request is not null && (long)request.Page * request.PageSize < count;
        using var step = ScenarioRecorder.Step("Then",
            $"the whole history has {count} events, so there {(more ? "are more pages" : "is no later page")}");
        if (historyPage() is not { } read) return;

        Verdicts.Check("events in all", read.TotalCount, count);
        Verdicts.Check("more pages", read.HasMore ? "yes" : "no", more ? "yes" : "no");
    }

    /// <summary>
    /// GH-5665. The entries of the page the last <see cref="WhenTheSagaHistoryIsRead{TSaga}" /> read have exactly these
    /// outcomes, in this order. Only a document saga's message history (<see cref="RecordSagaHistoryAttribute" />)
    /// records an outcome per entry. Reads "the history page's outcomes are Started, Compensated".
    /// </summary>
    public void ThenTheSagaHistoryOutcomesAre(params SagaHistoryOutcome[] outcomes)
    {
        using var step = ScenarioRecorder.Step("Then",
            $"the history page's outcomes are {string.Join(", ", outcomes)}");
        if (historyPage() is not { } page) return;

        var actual = page.Events
            .Select(x => x.Data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                         x.Data.TryGetProperty(nameof(SagaHistoryRecord.Outcome), out var outcome)
                ? outcome.GetString() ?? "none"
                : "none")
            .ToArray();

        Verdicts.Check("outcomes", string.Join(", ", actual), string.Join(", ", outcomes));
    }

    /// <summary>
    /// GH-5415. The saga that <see cref="WhenTheSagaIsReadAtVersion{TSaga}" /> or <see cref="WhenTheSagaIsReadAsOf{TSaga}" />
    /// read matches <paramref name="expected" />, a partial object on only the members it names. Reads
    /// "the OrderFulfillment saga was then IsPaid: true, AreItemsReserved: false".
    /// </summary>
    public void ThenTheSagaWas(object expected)
    {
        var values = expected is IPartialObject partial ? PartialObjects.DescribeValues(partial) : ScenarioValues.Describe(expected);
        var name = root._sagaAtType is { } type ? nameOf(type) : nameOf(PartialMatching.ExpectedType(expected));
        using var step = ScenarioRecorder.Step("Then", $"the {name} saga was then {values}");

        if (!acted || root._sagaAtType is null)
        {
            Verdicts.Fail("Expected a saga read at a version or a time, but no such read has run.");
            return;
        }

        if (LastAct.Error is { } failure)
        {
            Verdicts.Fail($"The read failed: {failure.GetType().Name}: {failure.Message}");
            return;
        }


        if (root._sagaAt is not { } state)
        {
            Verdicts.Fail($"There was no {name} saga at that point: its stream had not reached it.");
            return;
        }

        var saga = state.State.Deserialize(root._sagaAtType, SagaJson);
        judge(saga, expected);
    }

    /// <summary>
    /// GH-5415. Wolverine's description of the <typeparamref name="TSaga" /> saga (<c>SagaDescriptor.SupportsHistory</c>)
    /// says its history can be read. Reads "the OrderFulfillment saga's history can be read".
    /// </summary>
    public Task ThenTheSagaSupportsHistory<TSaga>() where TSaga : Saga
        => inStep(ScenarioRecorder.Step("Then", $"the {nameOf(typeof(TSaga))} saga's history can be read"), async () =>
        {
            var descriptor = await sagaDescriptorAsync<TSaga>();
            if (descriptor is null) return;

            Verdicts.Check("supports history", descriptor.SupportsHistory ? "yes" : "no", "yes");
        });

    private async Task readAct(string text, Type sagaType, Func<ISagaHistoryDiagnostics, Task> read, bool pointInTime = false)
    {
        recordTouched(sagaType);
        _historyPage = null;
        _sagaAt = null;
        _sagaAtType = pointInTime ? sagaType : null;
        NextActDispatchesNoMessage();

        await ActAsync(text, tracking => tracking.ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ =>
            sagaRuntime.SagaStorage is ISagaHistoryDiagnostics history
                ? read(history)
                : throw new NotSupportedException(
                    $"This service's saga storage ({sagaRuntime.SagaStorage.GetType().NameInCode()}) cannot read saga history."))));
    }

    private SagaHistoryPage? historyPage()
    {
        if (!acted || (root._historyPage is null && LastAct.Error is null))
        {
            Verdicts.Fail("Expected a page of saga history, but no history read has run.");
            return null;
        }

        if (LastAct.Error is { } failure)
        {
            Verdicts.Fail($"The history read failed: {failure.GetType().Name}: {failure.Message}");
            return null;
        }

        return root._historyPage;
    }

    private static string describeHistoryEvent(SagaHistoryEvent e)
        => e.DotNetTypeName is { } dotNet ? shortName(dotNet) : e.EventTypeName;

    private static bool isEventOf(SagaHistoryEvent e, Type type)
        => e.DotNetTypeName == type.FullName || e.DotNetTypeName == type.FullNameInCode() ||
           (e.DotNetTypeName is null && string.Equals(e.EventTypeName, type.Name, StringComparison.OrdinalIgnoreCase));

    // ---- completion, the snapshot and the saga explorer ------------------------------------------------------------

    /// <summary>
    /// GH-5415. The <typeparamref name="TSaga" /> saga with this identity is completed: it has a completion time
    /// (<see cref="Saga.CompletedAt" />), and it is still there. For an event-sourced saga the completion time is the
    /// timestamp of the last event of its stream, the terminal event. Reads "the OrderFulfillment saga #OrderFulfillment is completed".
    /// </summary>
    public Task ThenTheSagaIsCompleted<TSaga>(object sagaId) where TSaga : Saga
    {
        var text = $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is completed";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var saga = await actedSagaAsync<TSaga>(sagaId, text);
            if (saga is null) return;

            Verdicts.Check("completed", saga.IsCompleted() ? "yes" : "no", "yes");
            Verdicts.Fact(saga.CompletedAt.HasValue,
                $"Expected the {nameOf(typeof(TSaga))} saga to have a completion time, but CompletedAt is empty.");

            if (typeof(EventSourcedSaga).IsAssignableFrom(typeof(TSaga)) && saga.CompletedAt is { } completedAt)
            {
                var events = await streamOfAsync(sagaId, text, typeof(TSaga));
                if (events.LastOrDefault() is { } terminal)
                {
                    // Compared at the store's precision. The snapshot takes CompletedAt from the event in memory, which
                    // on Linux carries 100ns ticks, while PostgreSQL keeps microseconds; so the same instant can read
                    // back up to a microsecond apart (it passed on macOS, whose clock only resolves microseconds).
                    Verdicts.Fact((completedAt - terminal.Timestamp).Duration() < TimeSpan.FromMicroseconds(1),
                        $"Expected the {nameOf(typeof(TSaga))} saga to have completed when its terminal " +
                        $"{nameOf(terminal.EventType)} was recorded ({terminal.Timestamp:O}), but CompletedAt is {completedAt:O}.");
                }
            }
        });
    }

    /// <summary>
    /// GH-5415. The <typeparamref name="TSaga" /> saga with this identity exists and has not completed. Reads
    /// "the OrderFulfillment saga #OrderFulfillment is still open".
    /// </summary>
    public Task ThenTheSagaIsStillOpen<TSaga>(object sagaId) where TSaga : Saga
    {
        var text = $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is still open";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var saga = await actedSagaAsync<TSaga>(sagaId, text);
            if (saga is null) return;

            Verdicts.Check("completed", saga.IsCompleted() ? "yes" : "no", "no");
        });
    }

    /// <summary>
    /// GH-5415. The stored snapshot of the event-sourced <typeparamref name="TSaga" /> saga -- the document the store
    /// wrote, not a live aggregation of its stream -- matches <paramref name="expected" />. Reads
    /// "the OrderFulfillment saga #OrderFulfillment's snapshot matches TotalAmount: 100".
    /// </summary>
    public Task ThenTheSagaSnapshot<TSaga>(object sagaId, object expected) where TSaga : Saga
    {
        var values = expected is IPartialObject partial ? PartialObjects.DescribeValues(partial) : ScenarioValues.Describe(expected);
        var text = $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)}'s snapshot matches {values}";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            if (!acted)
            {
                Verdicts.Fail($"Expected a {nameOf(typeof(TSaga))} snapshot, but no act has run.");
                return;
            }

            var snapshot = await EventStoreAuthoring.LoadDocumentAsync<TSaga>(await storeForAsync(text, typeof(TSaga)), sagaId);
            if (snapshot is null)
            {
                Verdicts.Fail($"There is no stored {nameOf(typeof(TSaga))} snapshot with id {sagaId}.");
                return;
            }

            recordTouched(typeof(TSaga));
            judge(snapshot, expected);
        });
    }

    /// <summary>
    /// GH-5415. The event store keeps the <typeparamref name="TSaga" /> saga with this lifecycle: an
    /// <see cref="ProjectionLifecycle.Inline" /> snapshot written in the same transaction as its events (the default
    /// Wolverine registers), or a <see cref="ProjectionLifecycle.Live" /> aggregation with no snapshot. Read off the
    /// store's own description of its projections. Reads "the OrderFulfillment saga is kept as an Inline snapshot".
    /// </summary>
    public Task ThenTheSagaIsKeptAs<TSaga>(ProjectionLifecycle lifecycle) where TSaga : Saga
    {
        var text = lifecycle == ProjectionLifecycle.Live
            ? $"the {nameOf(typeof(TSaga))} saga is aggregated Live, with no snapshot"
            : $"the {nameOf(typeof(TSaga))} saga is kept as an {lifecycle} snapshot";

        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var store = await storeForAsync(text, typeof(TSaga));
            var usage = await store.TryCreateUsage(CancellationToken.None);
            var subscription = usage?.Subscriptions.FirstOrDefault(x =>
                x.AggregateType is { } aggregate && aggregate.FullName == typeof(TSaga).FullName);

            Verdicts.Check("lifecycle", subscription?.Lifecycle.ToString() ?? "(not projected)", lifecycle.ToString());
        });
    }

    /// <summary>
    /// GH-5415. The saga explorer -- <see cref="ISagaStoreDiagnostics.ReadSagaAsync" />, which CritterWatch's Saga
    /// Explorer calls -- opens the <typeparamref name="TSaga" /> saga with this identity, and it matches
    /// <paramref name="expected" />. Reads "the saga explorer shows the OrderFulfillment saga #OrderFulfillment with TotalAmount: 100".
    /// </summary>
    public Task ThenTheSagaExplorerShows<TSaga>(object sagaId, object expected) where TSaga : Saga
    {
        var values = expected is IPartialObject partial ? PartialObjects.DescribeValues(partial) : ScenarioValues.Describe(expected);
        var text = $"the saga explorer shows the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} with {values}";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var state = await sagaRuntime.SagaStorage.ReadSagaAsync(typeof(TSaga).FullNameInCode(), sagaId, CancellationToken.None);
            if (state is null)
            {
                Verdicts.Fail($"The saga explorer has no {nameOf(typeof(TSaga))} saga with id {sagaId}.");
                return;
            }

            recordTouched(typeof(TSaga));
            judge(state.State.Deserialize<TSaga>(SagaJson), expected);
        });
    }

    /// <summary>
    /// GH-5415. The saga explorer can search the <typeparamref name="TSaga" /> sagas
    /// (<see cref="ISagaStoreDiagnostics.GetQueryCapabilities" />), or with <paramref name="can" /> false, it can open
    /// one at a time but cannot search them. Reads "the saga explorer can search the OrderFulfillment sagas".
    /// </summary>
    public void ThenTheSagaExplorerCanSearch<TSaga>(bool can = true) where TSaga : Saga
    {
        using var step = ScenarioRecorder.Step("Then",
            $"the saga explorer {(can ? "can" : "cannot")} search the {nameOf(typeof(TSaga))} sagas");
        var capabilities = sagaRuntime.SagaStorage.GetQueryCapabilities(typeof(TSaga).FullNameInCode());
        Verdicts.Check("can search", capabilities.CanQuery ? "yes" : "no", can ? "yes" : "no");
    }

    // ---- retention (GH-5572) -----------------------------------------------------------------------------------------

    /// <summary>
    /// GH-5415 / GH-5572. The act: the saga retention sweep runs for <typeparamref name="TSaga" />, as the retention
    /// agent would <paramref name="later" /> from now: completed sagas whose retention period has passed by then are
    /// removed (an event-sourced saga's stream archived, its snapshot deleted). Reads
    /// "the completed OrderFulfillment sagas are swept 31 days later", or "... swept now".
    /// </summary>
    public Task WhenCompletedSagasAreSwept<TSaga>(TimeSpan later) where TSaga : Saga
    {
        var when = later <= TimeSpan.Zero ? "now" : $"{describe(later)} later";
        var text = $"the completed {nameOf(typeof(TSaga))} sagas are swept {when}";
        recordTouched(typeof(TSaga));
        NextActDispatchesNoMessage();

        return root.ActAsync(text, tracking => tracking.ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
        {
            var retention = SagaRetention.For(typeof(TSaga), sagaRuntime.Options)
                            ?? throw new InvalidOperationException(
                                $"{nameOf(typeof(TSaga))} sagas are not retained, so there is nothing to sweep. Retain them with " +
                                "opts.Policies.RetainCompletedSagas() or [RetainWhenCompleted].");

            // As the retention agents do: every storage that keeps the saga type sweeps it
            var sweepers = Services.GetServices<ISagaStoreDiagnostics>().OfType<ISagaRetentionSweeper>()
                .Where(x => x.SagaTypesForRetention().Contains(typeof(TSaga)))
                .ToArray();
            if (sweepers.Length == 0)
            {
                throw new InvalidOperationException($"No saga storage in this service sweeps {nameOf(typeof(TSaga))} sagas.");
            }

            foreach (var sweeper in sweepers)
            {
                await sweeper.DeleteCompletedSagasAsync(typeof(TSaga), DateTimeOffset.UtcNow.Add(later) - retention,
                    CancellationToken.None);
            }
        })));
    }

    /// <summary>
    /// GH-5415. The stream of the event-sourced <typeparamref name="TSaga" /> saga with this identity is archived: its
    /// events are kept, out of the way. Reads "the OrderFulfillment saga #OrderFulfillment's stream is archived".
    /// </summary>
    public Task ThenTheSagaStreamIsArchived<TSaga>(object sagaId) where TSaga : Saga => thenArchived<TSaga>(sagaId, true);

    /// <summary>
    /// GH-5415. The stream of the event-sourced <typeparamref name="TSaga" /> saga with this identity is not archived.
    /// Reads "the OrderFulfillment saga #OrderFulfillment's stream is not archived".
    /// </summary>
    public Task ThenTheSagaStreamIsNotArchived<TSaga>(object sagaId) where TSaga : Saga => thenArchived<TSaga>(sagaId, false);

    private Task thenArchived<TSaga>(object sagaId, bool archived) where TSaga : Saga
    {
        var text = $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)}'s stream is {(archived ? "" : "not ")}archived";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            if (!acted)
            {
                Verdicts.Fail($"Expected the {nameOf(typeof(TSaga))} saga's stream to be checked after an act, but no act has run.");
                return;
            }

            if (LastAct.Error is { } failure)
            {
                Verdicts.Fail($"The act failed: {failure.GetType().Name}: {failure.Message}");
                return;
            }

            var store = await storeForAsync(text, typeof(TSaga));
            var state = sagaId is Guid guid
                ? await EventStores.FetchStreamStateAsync(store, guid)
                : await EventStores.FetchStreamStateAsync(store, sagaId.ToString()!);

            if (state is null)
            {
                Verdicts.Fail($"There is no {nameOf(typeof(TSaga))} stream with id {sagaId}: its events are gone, not archived.");
                return;
            }

            Verdicts.Check("archived", state.IsArchived ? "yes" : "no", archived ? "yes" : "no");
        });
    }

    // ---- a service that refuses to start ---------------------------------------------------------------------------

    /// <summary>
    /// GH-5415. The act: a separate Wolverine service, configured by <paramref name="configure" />, is started and then
    /// stopped. Whether it started is what <see cref="ThenTheServiceStarted" /> and
    /// <see cref="ThenTheServiceRefusedToStart" /> judge; the spec's own host is not involved. Reads
    /// "a service starts with an OrderFulfillment saga keyed by an int".
    /// </summary>
    public async Task WhenAServiceStarts(string description, Action<WolverineOptions> configure)
    {
        await inStep(ScenarioRecorder.Step("When", $"a service starts {description}"), async () =>
        {
            root._serviceStartAttempted = true;
            root._serviceStartFailure = null;

            IHost? host = null;
            try
            {
                host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder().UseWolverine(configure).StartAsync();
            }
            catch (Exception e)
            {
                root._serviceStartFailure = e;
            }
            finally
            {
                if (host is not null)
                {
                    await host.StopAsync();
                    host.Dispose();
                }
            }
        });
    }

    /// <summary>GH-5415. The service <see cref="WhenAServiceStarts" /> started started. Reads "the service starts".</summary>
    public void ThenTheServiceStarted()
    {
        using var step = ScenarioRecorder.Step("Then", "the service starts");
        if (!serviceStartAttempted()) return;

        Verdicts.Fact(root._serviceStartFailure is null,
            $"Expected the service to start, but it refused: {describeFailure(root._serviceStartFailure)}");
    }

    /// <summary>
    /// GH-5415. The service <see cref="WhenAServiceStarts" /> started refused to start, saying <paramref name="reason" />
    /// and naming each of <paramref name="mentioning" /> (a type is named by its name). Searched through the
    /// exception's inner exceptions. Reads "the service refuses to start: event-sourced sagas need a Guid or string id".
    /// </summary>
    public void ThenTheServiceRefusedToStart(string reason, params object[] mentioning)
    {
        using var step = ScenarioRecorder.Step("Then", $"the service refuses to start: {reason}" +
                                                       (mentioning.Length == 0 ? "" : $", naming {string.Join(" and ", mentioning.Select(mentioned))}"));
        if (!serviceStartAttempted()) return;

        if (root._serviceStartFailure is not { } failure)
        {
            Verdicts.Fail($"Expected the service to refuse to start with \"{reason}\", but it started.");
            return;
        }

        var text = string.Join(Environment.NewLine, messagesOf(failure));
        Verdicts.Fact(text.Contains(reason, StringComparison.OrdinalIgnoreCase),
            $"Expected the service to refuse to start with \"{reason}\", but it said: {describeFailure(failure)}");

        foreach (var value in mentioning)
        {
            var name = mentioned(value);
            Verdicts.Fact(text.Contains(name, StringComparison.Ordinal),
                $"Expected the refusal to name {name}, but it said: {describeFailure(failure)}");
        }
    }

    private bool serviceStartAttempted()
    {
        if (root._serviceStartAttempted) return true;
        Verdicts.Fail("Expected a service to have been started, but no WhenAServiceStarts step has run.");
        return false;
    }

    private static string mentioned(object value) => value is Type type ? type.Name : value.ToString() ?? "";

    private static IEnumerable<string> messagesOf(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            yield return $"{current.GetType().Name}: {current.Message}";
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(messagesOf)) yield return inner;
            }
        }
    }

    private static string describeFailure(Exception? e) => e is null ? "(nothing)" : string.Join(" --> ", messagesOf(e));

    // ---- shared ----------------------------------------------------------------------------------------------------

    private async Task<TSaga?> actedSagaAsync<TSaga>(object sagaId, string text) where TSaga : Saga
    {
        if (!acted)
        {
            Verdicts.Fail($"Expected a {nameOf(typeof(TSaga))} saga with id {sagaId}, but no act has run.");
            return null;
        }

        var saga = await loadSagaAsync<TSaga>(sagaId, text);
        if (saga is null)
        {
            Verdicts.Fail($"No {nameOf(typeof(TSaga))} saga with id {sagaId}.");
            return null;
        }

        recordTouched(typeof(TSaga));
        return saga;
    }

    private async Task<IReadOnlyList<IEvent>> streamOfAsync(object sagaId, string text, Type sagaType)
    {
        var store = await storeForAsync(text, sagaType);
        return sagaId is Guid guid
            ? await EventStores.FetchStreamAsync(store, guid)
            : await EventStores.FetchStreamAsync(store, sagaId.ToString()!);
    }

    private async Task<Configuration.Capabilities.SagaDescriptor?> sagaDescriptorAsync<TSaga>() where TSaga : Saga
    {
        var sagas = await sagaRuntime.SagaStorage.GetRegisteredSagasAsync(CancellationToken.None);
        var descriptor = sagas.FirstOrDefault(x =>
            x.StateType.FullName == typeof(TSaga).FullNameInCode() || x.StateType.FullName == typeof(TSaga).FullName);
        if (descriptor is null)
        {
            Verdicts.Fail($"This service's saga storage does not describe a {nameOf(typeof(TSaga))} saga.");
        }

        return descriptor;
    }

    private static string describe(TimeSpan span)
        => span.TotalDays >= 1 && span.TotalDays == Math.Floor(span.TotalDays)
            ? $"{span.TotalDays:0} day{(span.TotalDays == 1 ? "" : "s")}"
            : span.ToString();
}
