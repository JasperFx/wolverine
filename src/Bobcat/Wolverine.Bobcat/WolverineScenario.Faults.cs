using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace Wolverine.Bobcat;

// GH-5553 / GH-5554 / GH-5556: vocabulary for specifying faults, dead letters and sagas across services.
// Written with the specifications in src/Samples/SagaFaults, wording settled by Jeremy on 2026-11-17 and 2026-11-18.
// The seams -- a crash right after a dead-letter move, a move that rolls back, another message saving a saga while it is
// compensated -- go through internal test hooks in Wolverine.
public partial class WolverineScenario
{
    private readonly Dictionary<string, IHost> _otherServices = new(StringComparer.OrdinalIgnoreCase);
    private bool _deadLettersFailTheAct = true;

    // ---- several services --------------------------------------------------------------------

    /// <summary>
    /// GH-5553. Every act also tracks <paramref name="service" />, a second Wolverine application -- usually
    /// another host in the same test process, connected over TCP or a broker -- and waits for what the act caused there
    /// too. Its messages are judged by the same Then steps, and named by its <c>ServiceName</c> in the steps that take
    /// one (<see cref="ThenDeadLettered{T}(string?)" />, <see cref="ThenHandled{T}(string)" />). Not a step: it says how the
    /// specification is run, as <see cref="Timeout" /> does.
    /// </summary>
    public void AlsoTrack(IHost service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var name = service.Services.GetRequiredService<IWolverineRuntime>().Options.ServiceName;
        root._otherServices[name] = service;
    }

    /// <summary>
    /// GH-5553. Whether a message that was dead-lettered while the act ran makes the act fail -- as any
    /// exception the tracked session saw always has. True by default. A specification about failures turns it off:
    /// a dead letter is then an outcome <see cref="ThenDeadLettered{T}(string?)" /> judges, and the act fails only on an
    /// exception that did not end in a dead letter.
    /// </summary>
    public bool DeadLettersFailTheAct
    {
        get => root._deadLettersFailTheAct;
        set => root._deadLettersFailTheAct = value;
    }

    // The first exception whose envelope was not dead-lettered within the session
    private static Exception? failureNotDeadLettered(ITrackedSession session)
    {
        var deadLettered = session.AllRecordsInOrder(MessageEventType.MovedToErrorQueue)
            .Select(x => x.Envelope?.Id).OfType<Guid>().ToHashSet();

        var record = session.AllRecordsInOrder()
            .FirstOrDefault(x => x.Exception is not null && (x.Envelope is null || !deadLettered.Contains(x.Envelope.Id)));
        if (record is not null) return record.Exception;

        // An exception the session captured with no record at all is never accounted for by a dead letter
        var recorded = session.AllRecordsInOrder().Select(x => x.Exception).OfType<Exception>()
            .ToHashSet(ReferenceEqualityComparer.Instance);
        return session.AllExceptions().FirstOrDefault(x => !recorded.Contains(x));
    }

    // ---- dead letters -------------------------------------------------------------------------

    /// <summary>
    /// GH-5553. A <typeparamref name="T" /> was dead-lettered (moved to the error queue) while the act ran -- by
    /// <paramref name="by" />, a service's <c>ServiceName</c>, when given. Judged whether or not the act failed: the
    /// failure is the story.
    /// </summary>
    public void ThenDeadLettered<T>(string? by = null)
    {
        using var step = ScenarioRecorder.Step("Then", deadLetteredText<T>(by, null));
        deadLettered<T>(by);
    }

    /// <summary>
    /// GH-5556. A <typeparamref name="T" /> was dead-lettered while the act ran, by <paramref name="by" /> when given,
    /// and its dead letter is tagged <paramref name="tagged" /> (<c>compensated</c>, <c>compensation-failed</c>,
    /// <c>compensation-saga-missing</c>). The tag is read back from that service's dead-letter storage, where it is a
    /// column of its own.
    /// </summary>
    public Task ThenDeadLettered<T>(string? by, string tagged)
    {
        ArgumentNullException.ThrowIfNull(tagged);
        return inStep(ScenarioRecorder.Step("Then", deadLetteredText<T>(by, tagged)), async () =>
        {
            var records = deadLettered<T>(by);
            if (records.Count == 0) return;

            var tags = new List<string?>();
            foreach (var record in records)
            {
                var runtime = runtimeOf(record.ServiceName);
                var stored = runtime is null || record.Envelope is null
                    ? null
                    : await runtime.Storage.DeadLetters.DeadLetterEnvelopeByIdAsync(record.Envelope.Id, record.Envelope.TenantId);
                tags.Add(stored?.Tag);
            }

            Verdicts.Check("tag", tags.FirstOrDefault(x => x == tagged) ?? tags.FirstOrDefault() ?? "(none)", tagged);
            messageFact(tags.Contains(tagged),
                $"Expected the dead-lettered {nameOf(typeof(T))} to be tagged \"{tagged}\", but it was tagged " +
                $"{string.Join(", ", tags.Distinct().Select(x => x is null ? "nothing" : $"\"{x}\""))}.");
        });
    }

    /// <summary>
    /// GH-5415. A <typeparamref name="T" /> was dead-lettered while the act ran -- by <paramref name="by" /> when given --
    /// because of a <typeparamref name="TException" /> or a subclass. Reads
    /// "PaymentReceived is dead-lettered by fulfillment for UnknownSagaException".
    /// </summary>
    public void ThenDeadLettered<T, TException>(string? by = null) where TException : Exception
    {
        using var step = ScenarioRecorder.Step("Then", deadLetteredText<T>(by, null) + $" for {nameOf(typeof(TException))}");
        var records = deadLettered<T>(by);
        if (records.Count == 0) return;

        var exceptions = records.Select(x => x.Exception).ToList();
        var match = exceptions.FirstOrDefault(x => x is TException);
        Verdicts.Check("exception", (match ?? exceptions.FirstOrDefault())?.GetType().Name ?? "(none recorded)",
            nameOf(typeof(TException)));
        messageFact(match is not null,
            $"Expected the dead-lettered {nameOf(typeof(T))} to have failed with {nameOf(typeof(TException))}, but it failed with " +
            $"{string.Join(", ", exceptions.Select(x => x is null ? "nothing recorded" : $"{x.GetType().Name}: {x.Message}"))}.");
    }

    private static string deadLetteredText<T>(string? by, string? tagged)
        => $"{nameOf(typeof(T))} is dead-lettered" + (by is null ? "" : $" by {by}") +
           (tagged is null ? "" : $", tagged \"{tagged}\"");

    // The step in progress judges that a T was dead-lettered, by the named service when given; the records when it was
    private IReadOnlyList<EnvelopeRecord> deadLettered<T>(string? by)
    {
        if (!acted)
        {
            Verdicts.Fail($"Expected a {nameOf(typeof(T))} to be dead-lettered, but no act has run.");
            return [];
        }

        var records = recordsOf(MessageEventType.MovedToErrorQueue, typeof(T))
            .Where(x => by is null || string.Equals(x.ServiceName, by, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(x => x.Envelope?.Id).ToList();
        Verdicts.Check("dead-lettered", records.Count > 0 ? "yes" : "no", "yes");
        messageFact(records.Count > 0,
            $"Expected a {nameOf(typeof(T))} to be dead-lettered{(by is null ? "" : $" by {by}")}, but {whatHappenedTo(typeof(T), out _)}{actFailure()}");
        return records;
    }

    // The Wolverine runtime of a service this specification tracks, by its ServiceName
    private IWolverineRuntime? runtimeOf(string? serviceName)
    {
        var own = Services.GetRequiredService<IWolverineRuntime>();
        if (serviceName is null || string.Equals(own.Options.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase)) return own;
        return root._otherServices.TryGetValue(serviceName, out var host)
            ? host.Services.GetRequiredService<IWolverineRuntime>()
            : null;
    }

    /// <summary>GH-5553. No <typeparamref name="T" /> was dead-lettered while the act ran, by <paramref name="by" /> when given.</summary>
    public void ThenNotDeadLettered<T>(string? by = null)
    {
        using var step = ScenarioRecorder.Step("Then", $"no {nameOf(typeof(T))} is dead-lettered{(by is null ? "" : $" by {by}")}");
        if (!canJudgeAnAbsence($"Expected no {nameOf(typeof(T))} to be dead-lettered")) return;

        var deadLettered = recordsOf(MessageEventType.MovedToErrorQueue, typeof(T))
            .Where(x => by is null || string.Equals(x.ServiceName, by, StringComparison.OrdinalIgnoreCase)).ToList();
        messageFact(deadLettered.Count == 0,
            $"Expected no {nameOf(typeof(T))} to be dead-lettered, but {deadLettered.Count} was, by " +
            $"{string.Join(", ", deadLettered.Select(x => x.ServiceName).Distinct())}: " +
            $"{describeFailures(failuresOf(typeof(T)))}.");
    }

    // ---- handled by a service -------------------------------------------------------------------

    /// <summary>
    /// GH-5553. A <typeparamref name="T" /> was handled successfully by <paramref name="by" />, a service's
    /// <c>ServiceName</c>: "Fault&lt;CheckStock&gt; is handled by orders". Judged whether or not the act failed.
    /// </summary>
    public void ThenHandled<T>(string by)
    {
        var service = by;
        using var step = ScenarioRecorder.Step("Then", $"{nameOf(typeof(T))} is handled by {service}");
        if (!acted)
        {
            Verdicts.Fail($"Expected a {nameOf(typeof(T))} to be handled by {service}, but no act has run.");
            return;
        }

        var handled = succeededIn(typeof(T), service);
        Verdicts.Check("handled", handled > 0 ? "yes" : "no", "yes");
        if (handled > 0) return;

        var elsewhere = recordsOf(MessageEventType.ExecutionStarted, typeof(T)).Select(x => x.ServiceName).Distinct().ToArray();
        Verdicts.Fail($"Expected a {nameOf(typeof(T))} to be handled by {service}, but " +
                      (elsewhere.Length > 0 && !elsewhere.Contains(service, StringComparer.OrdinalIgnoreCase)
                          ? $"it ran only in {string.Join(", ", elsewhere)}."
                          : whatHappenedTo(typeof(T), out _) + actFailure()) +
                      " " + activitySummary(LastAct));
    }

    /// <summary>GH-5553. No <typeparamref name="T" /> was handled by <paramref name="by" />, a service's <c>ServiceName</c>: no execution of it even started there.</summary>
    public void ThenNotHandled<T>(string by)
    {
        var service = by;
        using var step = ScenarioRecorder.Step("Then", $"no {nameOf(typeof(T))} is handled by {service}");
        if (!canJudgeAnAbsence($"Expected no {nameOf(typeof(T))} to be handled by {service}")) return;

        var ran = recordsOf(MessageEventType.ExecutionStarted, typeof(T))
            .Count(x => string.Equals(x.ServiceName, service, StringComparison.OrdinalIgnoreCase));
        messageFact(ran == 0, $"Expected no {nameOf(typeof(T))} to be handled by {service}, but its handler ran there {times(ran)}.");
    }

    /// <summary>
    /// GH-5556. A <typeparamref name="T" /> was attempted exactly <paramref name="count" /> times -- by
    /// <paramref name="by" /> when given -- counting the first attempt and every retry. Judged whether or not the act failed.
    /// </summary>
    public void ThenAttempted<T>(int count, string? by = null)
    {
        using var step = ScenarioRecorder.Step("Then", $"{nameOf(typeof(T))} is attempted {times(count)}{(by is null ? "" : $" by {by}")}");
        if (!acted)
        {
            Verdicts.Fail($"Expected a {nameOf(typeof(T))} to be attempted, but no act has run.");
            return;
        }

        var attempts = recordsOf(MessageEventType.ExecutionStarted, typeof(T))
            .Where(x => by is null || string.Equals(x.ServiceName, by, StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.Envelope?.Id, x.AttemptNumber)).Distinct().Count();
        Verdicts.Check("attempts", attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        messageFact(attempts == count, $"Expected {nameOf(typeof(T))} to be attempted {times(count)}, but it was attempted {times(attempts)}.");
    }

    // The act's own failure, as a sentence after one that already ended
    private string actFailure() => failureSuffix() is { Length: > 0 } suffix ? " " + suffix.TrimStart('.', ' ') + "." : "";

    private int succeededIn(Type messageType, string service)
        => recordsOf(MessageEventType.MessageSucceeded, messageType)
            .Count(x => string.Equals(x.ServiceName, service, StringComparison.OrdinalIgnoreCase));

    // ---- sagas ----------------------------------------------------------------------------------

    /// <summary>
    /// GH-5553. The <typeparamref name="TSaga" /> saga with this identity handled a <typeparamref name="TMessage" />
    /// successfully: an execution of it completed for an envelope carrying that saga id. Judged whether or not the act
    /// failed.
    /// </summary>
    public void ThenTheSagaHandled<TSaga, TMessage>(object sagaId) where TSaga : Saga
    {
        using var step = ScenarioRecorder.Step("Then",
            $"the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} handles {nameOf(typeof(TMessage))}");
        if (!acted)
        {
            Verdicts.Fail($"Expected the {nameOf(typeof(TSaga))} saga to handle a {nameOf(typeof(TMessage))}, but no act has run.");
            return;
        }

        var id = sagaId.ToString();
        var named = ScenarioValues.Format(sagaId);
        var handled = recordsOf(MessageEventType.MessageSucceeded, typeof(TMessage)).Count(x => x.Envelope?.SagaId == id);
        Verdicts.Check("handled", handled > 0 ? "yes" : "no", "yes");
        if (handled > 0) return;

        var otherSagas = recordsOf(MessageEventType.ExecutionStarted, typeof(TMessage))
            .Select(x => x.Envelope?.SagaId).Distinct().ToArray();
        Verdicts.Fail($"Expected the {nameOf(typeof(TSaga))} saga {named} to handle a {nameOf(typeof(TMessage))}, but " +
                      (otherSagas.Length > 0
                          ? $"it ran only for saga id(s) {string.Join(", ", otherSagas.Select(x => x ?? "(none)"))}" +
                            $"{(failuresOf(typeof(TMessage)).Count > 0 ? $": {describeFailures(failuresOf(typeof(TMessage)))}" : "")}."
                          : whatHappenedTo(typeof(TMessage), out _) + actFailure()) +
                      " " + activitySummary(LastAct));
    }

    /// <summary>
    /// GH-4484, folded into GH-5553. Every <typeparamref name="T" /> sent while the act ran carried the group id
    /// <paramref name="groupId" />: what partitioned messaging, or <c>UseInferredMessageGrouping()</c>, assigned it.
    /// </summary>
    public void ThenGroupedBy<T>(object groupId)
    {
        using var step = ScenarioRecorder.Step("Then", $"{nameOf(typeof(T))} is grouped by {ScenarioValues.Format(groupId)}");
        if (!acted)
        {
            Verdicts.Fail($"Expected a {nameOf(typeof(T))} to be grouped, but no act has run.");
            return;
        }

        var sent = LastAct.Session?.Sent.RecordsInOrder().Select(x => x.Envelope)
            .Where(x => x?.Message is not null && typeof(T).IsInstanceOfType(x.Message)).OfType<Envelope>().ToList() ?? [];
        var expected = groupId.ToString();
        Verdicts.Check("group", sent.FirstOrDefault()?.GroupId ?? "(none)", expected!);
        if (!messageFact(sent.Count > 0, $"Expected a {nameOf(typeof(T))} to be sent, but {whatHappenedTo(typeof(T), out _)}{actFailure()}")) return;

        messageFact(sent.All(x => x.GroupId == expected),
            $"Expected every {nameOf(typeof(T))} to be grouped by {ScenarioValues.Format(groupId)}, but the group id was " +
            $"{string.Join(", ", sent.Select(x => x.GroupId ?? "(none)").Distinct())}.");
    }

    // ---- seams in Wolverine's dead-letter move and saga compensation (internal test hooks) ---------------------

    private List<ITrackedCondition> _actConditions = [];
    private List<Action> _afterAct = [];

    private WolverineRuntime wolverineRuntimeOf(string service, string step)
        => runtimeOf(service) as WolverineRuntime
           ?? throw new SpecCriticalException($"'{step}': this specification does not track a service named '{service}'. Track it with AlsoTrack(host).");

    /// <summary>
    /// GH-5554. The service named <paramref name="service" /> crashes right after its next dead-letter move commits,
    /// before it sends anything else: the faults written with the dead letter stay in its outbox. Reads
    /// "inventory crashes right after its next dead-letter move commits".
    /// </summary>
    public void GivenTheServiceCrashesAfterItsNextDeadLetter(string service)
    {
        var text = $"{service} crashes right after its next dead-letter move commits";
        using var step = ScenarioRecorder.Step("Given", text);
        var hooks = wolverineRuntimeOf(service, text).DeadLetterHooks;
        hooks.Reset();
        hooks.CrashAfterNextMove();
    }

    /// <summary>
    /// GH-5554. The service named <paramref name="service" />, crashed by
    /// <see cref="GivenTheServiceCrashesAfterItsNextDeadLetter" />, restarts: what it owned in its inbox and outbox
    /// is released and recovered, as it is for a node that went away. The new act: it waits until every fault the crash
    /// left behind has been handled. Reads "inventory restarts and recovers its outbox".
    /// </summary>
    public Task WhenTheServiceRestarts(string service)
    {
        var text = $"{service} restarts and recovers its outbox";
        var runtime = wolverineRuntimeOf(service, text);
        var stranded = runtime.DeadLetterHooks.Stranded.Select(x => x.Id).ToArray();

        return root.ActAsync(text, tracking => ownCorrelation(null, tracking, null)
            .WaitForCondition(new EnvelopesFinished(stranded))
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                runtime.DeadLetterHooks.Reset();
                await runtime.Storage.Admin.ReleaseAllOwnershipAsync(runtime.DurabilitySettings.AssignedNodeNumber);
            })));
    }

    /// <summary>
    /// GH-5554. The next dead-letter move in the service named <paramref name="service" /> fails after its statements
    /// ran, so its transaction rolls back with everything written in it. The act then waits until that service has
    /// handed the message back to its inbox. Reads "the next dead-letter move in inventory fails and rolls back".
    /// </summary>
    public void GivenTheNextDeadLetterMoveFails(string service)
    {
        var text = $"the next dead-letter move in {service} fails and rolls back";
        using var step = ScenarioRecorder.Step("Given", text);
        var hooks = wolverineRuntimeOf(service, text).DeadLetterHooks;
        hooks.Reset();
        hooks.FailNextMove();

        root._actConditions.Add(new RequeuedIn(service));
        root._afterAct.Add(hooks.Reset);
    }

    /// <summary>
    /// GH-5556. While the <typeparamref name="TSaga" /> saga with this identity is compensated, another message saves it
    /// first -- once, or on every attempt -- so saving the compensated saga meets a concurrency conflict. Reads
    /// "another message saves the Order saga #OrderId while it is being compensated, once".
    /// </summary>
    public void WhileCompensatingTheSagaIsChanged<TSaga>(object sagaId, bool onEveryAttempt = false) where TSaga : Saga
    {
        var text = $"another message saves the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} while it is being compensated" +
                   (onEveryAttempt ? ", on every attempt" : ", once");
        using var step = ScenarioRecorder.Step("Given", text);

        var hooks = ((WolverineRuntime)sagaRuntime).CompensationHooks;
        var id = sagaId.ToString();
        var saved = 0;
        hooks.BeforeSave = async saga =>
        {
            if (saga is not TSaga || sagaIdOf(saga).ToString() != id) return;
            if (!onEveryAttempt && Interlocked.Increment(ref saved) > 1) return;

            var current = await loadSagaAsync<TSaga>(sagaId, text);
            if (current != null) await updateSagaAsync(current, text);
        };
        root._afterAct.Add(() => hooks.BeforeSave = null);
    }

    /// <summary>
    /// GH-5415. While the event-sourced <typeparamref name="TSaga" /> saga with this identity is compensated, another
    /// message appends <paramref name="event" /> to its stream, after the compensation loaded it and before it saves, so
    /// the compensation's save meets a concurrency conflict -- once, or on every attempt. Reads
    /// "another message appends ItemsReserved to the OrderFulfillment saga #OrderFulfillmentId's stream while it is being
    /// compensated, once".
    /// </summary>
    public void WhileCompensatingAnotherMessageAppends<TSaga>(object sagaId, object @event, bool onEveryAttempt = false)
        where TSaga : EventSourcedSaga
    {
        var text = $"another message appends {nameOf(PartialMatching.ExpectedType(@event))} to the {nameOf(typeof(TSaga))} saga " +
                   $"{ScenarioValues.Format(sagaId)}'s stream while it is being compensated" + (onEveryAttempt ? ", on every attempt" : ", once");
        using var step = ScenarioRecorder.Step("Given", text);

        var built = Build(@event, text);
        var hooks = ((WolverineRuntime)sagaRuntime).CompensationHooks;
        var id = sagaId.ToString();
        var appended = 0;
        hooks.BeforeSave = async saga =>
        {
            if (saga is not TSaga || sagaIdOf(saga).ToString() != id) return;
            if (!onEveryAttempt && Interlocked.Increment(ref appended) > 1) return;

            await EventStoreAuthoring.AppendAsync(await storeForAsync(text, typeof(TSaga)), typeof(TSaga), sagaId, [built]);
        };
        root._afterAct.Add(() => hooks.BeforeSave = null);
    }

    /// <summary>
    /// GH-5556. While the <typeparamref name="TSaga" /> saga with this identity is about to be compensated, another
    /// message completes it, so compensation finds no saga. Reads
    /// "while the Order saga #OrderId is about to be compensated, another message completes it".
    /// </summary>
    public void WhileCompensatingTheSagaIsCompleted<TSaga>(object sagaId) where TSaga : Saga
    {
        var text = $"while the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is about to be compensated, another message completes it";
        using var step = ScenarioRecorder.Step("Given", text);

        var hooks = ((WolverineRuntime)sagaRuntime).CompensationHooks;
        var id = sagaId.ToString();
        hooks.BeforeCompensating = async (sagaType, compensatedId) =>
        {
            if (sagaType != typeof(TSaga) || compensatedId.ToString() != id) return;

            var current = await loadSagaAsync<TSaga>(sagaId, text);
            if (current != null) await deleteSagaAsync(current, text);
        };
        root._afterAct.Add(() => hooks.BeforeCompensating = null);
    }

    // ---- the inbox and outbox of a service (GH-5554) --------------------------------------------------------------

    /// <summary>
    /// GH-5554. A <typeparamref name="T" /> addressed to the service named <paramref name="to" /> is waiting in the
    /// durable outbox of the service named <paramref name="by" />: written, not sent yet. Reads
    /// "a Fault&lt;ReserveInventory&gt; to orders waits in inventory's outbox".
    /// </summary>
    public Task ThenWaitingInTheOutbox<T>(string by, string to)
    {
        var text = $"{withArticle(nameOf(typeof(T)))} to {to} waits in {by}'s outbox";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var runtime = wolverineRuntimeOf(by, text);
            var addresses = listenerAddressesOf(wolverineRuntimeOf(to, text));
            var waiting = (await outgoingOf<T>(runtime)).ToList();
            var toService = waiting.Where(x => x.Destination != null && addresses.Contains(normalize(x.Destination))).ToList();

            Verdicts.Check("waiting", toService.Count > 0 ? "yes" : "no", "yes");
            Verdicts.Fact(toService.Count > 0, waiting.Count == 0
                ? $"Expected {withArticle(nameOf(typeof(T)))} to {to} in {by}'s outbox, but there is none."
                : $"Expected {withArticle(nameOf(typeof(T)))} to {to} in {by}'s outbox, but the ones there go to " +
                  $"{string.Join(", ", waiting.Select(x => x.Destination).Distinct())}.");
        });
    }

    /// <summary>
    /// GH-5554. No <typeparamref name="T" /> is waiting in the durable outbox of the service named
    /// <paramref name="by" />, once what it was sending has been sent. Reads
    /// "no Fault&lt;ReserveInventory&gt; waits in inventory's outbox".
    /// </summary>
    public Task ThenNothingWaitingInTheOutbox<T>(string by)
    {
        var text = $"no {nameOf(typeof(T))} waits in {by}'s outbox";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var runtime = wolverineRuntimeOf(by, text);

            // A sent envelope leaves the outbox right after it is sent, which can be just after the act ended
            var waiting = await eventuallyAsync(() => outgoingOf<T>(runtime), x => x.Count == 0);
            Verdicts.Fact(waiting.Count == 0,
                $"Expected no {nameOf(typeof(T))} in {by}'s outbox, but {waiting.Count} still wait there, to " +
                $"{string.Join(", ", waiting.Select(x => x.Destination).Distinct())}.");
        });
    }

    /// <summary>
    /// GH-5554. The <typeparamref name="T" /> is still in the durable inbox of the service named <paramref name="by" />,
    /// neither handled nor dead-lettered, so it will be tried again. Reads "ReserveInventory is still in inventory's inbox".
    /// </summary>
    public Task ThenStillInTheInbox<T>(string by)
    {
        var text = $"{nameOf(typeof(T))} is still in {by}'s inbox";
        return inStep(ScenarioRecorder.Step("Then", text), async () =>
        {
            var runtime = wolverineRuntimeOf(by, text);
            var messageType = typeof(T).ToMessageTypeName();

            var incoming = await eventuallyAsync(
                async () => (await runtime.Storage.Admin.AllIncomingAsync())
                    .Where(x => x.MessageType == messageType && x.Status != EnvelopeStatus.Handled).ToList(),
                x => x.Count > 0);

            Verdicts.Check("in the inbox", incoming.Count > 0 ? "yes" : "no", "yes");
            Verdicts.Fact(incoming.Count > 0, $"Expected a {nameOf(typeof(T))} to be waiting in {by}'s inbox, but there is none.");
        });
    }

    private static async Task<IReadOnlyList<Envelope>> outgoingOf<T>(IWolverineRuntime runtime)
    {
        var messageType = typeof(T).ToMessageTypeName();
        return (await runtime.Storage.Admin.AllOutgoingAsync()).Where(x => x.MessageType == messageType).ToList();
    }

    private static HashSet<string> listenerAddressesOf(IWolverineRuntime runtime)
        => runtime.Endpoints.ActiveListeners().Select(x => normalize(x.Uri)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string normalize(Uri uri) => uri.ToString().TrimEnd('/');

    // Polls for a short while, for what a service finishes just after the act
    private static async Task<T> eventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var value = await read();
        for (var i = 0; i < 50 && !done(value); i++)
        {
            await Task.Delay(100);
            value = await read();
        }

        return value;
    }

    // The act's session waits until each of these envelopes finished, wherever it ran
    private sealed class EnvelopesFinished(IReadOnlyCollection<Guid> ids) : ITrackedCondition
    {
        private readonly HashSet<Guid> _remaining = ids.ToHashSet();

        public void Record(EnvelopeRecord record)
        {
            if (record.Envelope is null) return;
            if (record.MessageEventType is MessageEventType.MessageSucceeded or MessageEventType.MovedToErrorQueue
                or MessageEventType.MessageFailed)
            {
                lock (_remaining) _remaining.Remove(record.Envelope.Id);
            }
        }

        public bool IsCompleted()
        {
            lock (_remaining) return _remaining.Count == 0;
        }

        public override string ToString() => $"Every envelope a crash left in the outbox is handled ({_remaining.Count} remaining)";
    }

    // The act's session waits until the named service hands a message back to be tried again
    private sealed class RequeuedIn(string service) : ITrackedCondition
    {
        private bool _requeued;

        public void Record(EnvelopeRecord record)
        {
            if (record.MessageEventType == MessageEventType.Requeued &&
                string.Equals(record.ServiceName, service, StringComparison.OrdinalIgnoreCase))
            {
                _requeued = true;
            }
        }

        public bool IsCompleted() => _requeued;

        public override string ToString() => $"A message is handed back to be tried again in {service}";
    }
}
