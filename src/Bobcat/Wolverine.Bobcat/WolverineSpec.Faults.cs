using Bobcat;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Bobcat;

// GH-5553 / GH-5554 / GH-5556: the PROPOSED vocabulary of WolverineScenario.Faults.cs, as members of the spec base class.
// See SPEC-REVIEW-5553.md.
public abstract partial class WolverineSpec
{
    // ---- several services -------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.AlsoTrack" />
    public void AlsoTrack(IHost service) => Scenario.AlsoTrack(service);

    /// <inheritdoc cref="WolverineScenario.DeadLettersFailTheAct" />
    public bool DeadLettersFailTheAct
    {
        get => Scenario.DeadLettersFailTheAct;
        set => Scenario.DeadLettersFailTheAct = value;
    }

    // ---- dead letters and handling by service -----------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenDeadLettered{T}" />
    public void ThenDeadLettered<T>(string? by = null, string? tagged = null) => Scenario.ThenDeadLettered<T>(by, tagged);

    /// <inheritdoc cref="WolverineScenario.ThenNotDeadLettered{T}" />
    public void ThenNotDeadLettered<T>(string? by = null) => Scenario.ThenNotDeadLettered<T>(by);

    /// <inheritdoc cref="WolverineScenario.ThenHandledBy{T}" />
    public void ThenHandledBy<T>(string service) => Scenario.ThenHandledBy<T>(service);

    /// <inheritdoc cref="WolverineScenario.ThenNotHandledBy{T}" />
    public void ThenNotHandledBy<T>(string service) => Scenario.ThenNotHandledBy<T>(service);

    /// <inheritdoc cref="WolverineScenario.ThenAttempted{T}" />
    public void ThenAttempted<T>(int count, string? by = null) => Scenario.ThenAttempted<T>(count, by);

    /// <inheritdoc cref="WolverineScenario.ThenGroupedBy{T}" />
    public void ThenGroupedBy<T>(object groupId) => Scenario.ThenGroupedBy<T>(groupId);

    // ---- sagas ------------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaHandled{TSaga, TMessage}" />
    public void ThenTheSagaHandled<TSaga, TMessage>(object sagaId) where TSaga : Saga
        => Scenario.ThenTheSagaHandled<TSaga, TMessage>(sagaId);

    /// <summary>
    /// PROPOSED (GH-5556). Arrange a saga in the state the act starts from. Today the saga's document store's
    /// <see cref="CritterStackSpec.GivenReadModel{T}(object)" />, and its wording; storage-agnostic (lightweight tables,
    /// EF Core) and its own wording once the proposal is settled.
    /// </summary>
    public Task GivenSaga<TSaga>(object saga) where TSaga : Saga => Scenario.GivenReadModel<TSaga>(saga);

    /// <summary>
    /// PROPOSED (GH-5556). The saga with this identity, judged against <paramref name="expected" />. Today the saga's
    /// document store's <see cref="CritterStackSpec.ThenDocument{T}(object, object)" />, and its wording.
    /// </summary>
    public Task<TSaga> ThenSaga<TSaga>(object sagaId, object expected) where TSaga : Saga
        => Scenario.ThenDocument<TSaga>(sagaId, expected);

    /// <summary>
    /// PROPOSED (GH-5556). No saga with this identity exists: it was completed, or never started. Today the saga's
    /// document store's <see cref="CritterStackSpec.ThenNoReadModel{T}(object)" />, and its wording.
    /// </summary>
    public Task ThenNoSaga<TSaga>(object sagaId) where TSaga : Saga => Scenario.ThenNoReadModel<TSaga>(sagaId);

    // ---- seams (stubs) ----------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.GivenTheServiceDiesAfterItsNextDeadLetter" />
    public void GivenTheServiceDiesAfterItsNextDeadLetter(string service) => Scenario.GivenTheServiceDiesAfterItsNextDeadLetter(service);

    /// <inheritdoc cref="WolverineScenario.WhenTheServiceRecovers" />
    public Task WhenTheServiceRecovers(string service) => Scenario.WhenTheServiceRecovers(service);

    /// <inheritdoc cref="WolverineScenario.GivenTheNextDeadLetterMoveFails" />
    public void GivenTheNextDeadLetterMoveFails(string service) => Scenario.GivenTheNextDeadLetterMoveFails(service);

    /// <inheritdoc cref="WolverineScenario.WhileCompensatingTheSagaIsChanged{TSaga}" />
    public void WhileCompensatingTheSagaIsChanged<TSaga>(object sagaId, bool onEveryAttempt = false) where TSaga : Saga
        => Scenario.WhileCompensatingTheSagaIsChanged<TSaga>(sagaId, onEveryAttempt);

    /// <inheritdoc cref="WolverineScenario.WhileCompensatingTheSagaIsCompleted{TSaga}" />
    public void WhileCompensatingTheSagaIsCompleted<TSaga>(object sagaId) where TSaga : Saga
        => Scenario.WhileCompensatingTheSagaIsCompleted<TSaga>(sagaId);
}
