using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

// GH-5553 / GH-5554 / GH-5556: vocabulary for specifying faults, dead letters and sagas across services.
// Written with the specifications in src/Samples/SagaFaults, wording settled by Jeremy on 2026-11-17. The steps that
// read the tracked session work; the ones that need a seam in Wolverine (a crash after the dead-letter move, a write
// that lands while a saga is compensated) are stubs until GH-5554 and GH-5556 are built: they render and fail, saying
// so, and let the rest of the specification render.
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

    // ---- seams: stubs until Wolverine has them ----------------------------------------------------

    /// <summary>
    /// GH-5554, NOT BUILT YET. The service named <paramref name="service" /> dies right after its next dead-letter
    /// move commits, before it can send anything else -- the crash window a durable fault closes.
    /// </summary>
    public void GivenTheServiceDiesAfterItsNextDeadLetter(string service)
        => notBuilt("Given", $"{service} dies right after its next dead-letter move commits",
            "GH-5554: it needs a seam in Wolverine's dead-letter move that stops the service once the move commits");

    /// <summary>
    /// GH-5554, NOT BUILT YET. The service named <paramref name="service" />, stopped by
    /// <see cref="GivenTheServiceDiesAfterItsNextDeadLetter" />, starts again and recovers its durable inbox and outbox.
    /// The new act.
    /// </summary>
    public Task WhenTheServiceRecovers(string service)
    {
        notBuilt("When", $"{service} starts again and recovers its outbox",
            "GH-5554: it restarts the service GivenTheServiceDiesAfterItsNextDeadLetter stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// GH-5554, NOT BUILT YET. The next dead-letter move in the service named <paramref name="service" /> fails
    /// and rolls back, so whatever was written in its transaction is gone too.
    /// </summary>
    public void GivenTheNextDeadLetterMoveFails(string service)
        => notBuilt("Given", $"the next dead-letter move in {service} fails and rolls back",
            "GH-5554: it needs a seam in Wolverine's dead-letter move that fails its transaction");

    /// <summary>
    /// GH-5556, NOT BUILT YET. While the <typeparamref name="TSaga" /> saga with this identity is compensated,
    /// another message saves it first -- once, or on every attempt -- so saving the compensated saga meets a
    /// concurrency conflict.
    /// </summary>
    public void WhileCompensatingTheSagaIsChanged<TSaga>(object sagaId, bool onEveryAttempt = false) where TSaga : Saga
        => notBuilt("Given",
            $"while the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is compensated, another message changes it first" +
            (onEveryAttempt ? ", on every attempt" : ", once"),
            "GH-5556: it needs a seam between loading the saga for compensation and saving it, as WhileActing has for events");

    /// <summary>
    /// GH-5556, NOT BUILT YET. While the <typeparamref name="TSaga" /> saga with this identity is about to be
    /// compensated, another message completes it, so compensation finds no saga.
    /// </summary>
    public void WhileCompensatingTheSagaIsCompleted<TSaga>(object sagaId) where TSaga : Saga
        => notBuilt("Given",
            $"while the {nameOf(typeof(TSaga))} saga {ScenarioValues.Format(sagaId)} is about to be compensated, another message completes it",
            "GH-5556: it needs a seam between the failure and compensation loading the saga");

    // ---- proposed vocabulary for GH-5554 (spec review of 2026-11-18): renders, then fails ------------------------

    /// <summary>
    /// GH-5554, PROPOSED, NOT BUILT YET. A <typeparamref name="T" /> addressed to the service named <paramref name="to" />
    /// is waiting in the durable outbox of the service named <paramref name="by" />: written, not sent yet. Reads
    /// "a Fault&lt;ReserveInventory&gt; to orders waits in inventory's outbox".
    /// </summary>
    public void ThenWaitingInTheOutbox<T>(string by, string to)
        => notBuilt("Then", $"{withArticle(nameOf(typeof(T)))} to {to} waits in {by}'s outbox",
            "proposed Bobcat vocabulary: it reads the service's outgoing envelopes from its message store and matches " +
            "their destination against the named service's listeners");

    /// <summary>
    /// GH-5554, PROPOSED, NOT BUILT YET. No <typeparamref name="T" /> is waiting in the durable outbox of the service
    /// named <paramref name="by" />. Reads "no Fault&lt;ReserveInventory&gt; waits in inventory's outbox".
    /// </summary>
    public void ThenNothingWaitingInTheOutbox<T>(string by)
        => notBuilt("Then", $"no {nameOf(typeof(T))} waits in {by}'s outbox",
            "proposed Bobcat vocabulary: it reads the service's outgoing envelopes from its message store");

    /// <summary>
    /// GH-5554, PROPOSED, NOT BUILT YET. The <typeparamref name="T" /> is still in the durable inbox of the service named
    /// <paramref name="by" />, neither handled nor dead-lettered, so it will be tried again. Reads
    /// "ReserveInventory is still in inventory's inbox".
    /// </summary>
    public void ThenStillInTheInbox<T>(string by)
        => notBuilt("Then", $"{nameOf(typeof(T))} is still in {by}'s inbox",
            "proposed Bobcat vocabulary: it reads the service's incoming envelopes from its message store");

    // Renders the step and fails it, without stopping the specification, so the steps after it still render for review
    private static void notBuilt(string keyword, string text, string because)
    {
        using var step = ScenarioRecorder.Step(keyword, text);
        Verdicts.Fail($"Not built yet ({because}).");
    }
}
