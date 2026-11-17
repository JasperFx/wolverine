using Bobcat;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Bobcat;

// GH-5553 / GH-5554 / GH-5556: the vocabulary of WolverineScenario.Faults.cs and WolverineScenario.Sagas.cs, as members of
// the spec base class.
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

    /// <inheritdoc cref="WolverineScenario.ThenDeadLettered{T}(string?)" />
    public void ThenDeadLettered<T>(string? by = null) => Scenario.ThenDeadLettered<T>(by);

    /// <inheritdoc cref="WolverineScenario.ThenDeadLettered{T}(string?, string)" />
    public Task ThenDeadLettered<T>(string? by, string tagged) => Scenario.ThenDeadLettered<T>(by, tagged);

    /// <inheritdoc cref="WolverineScenario.ThenNotDeadLettered{T}" />
    public void ThenNotDeadLettered<T>(string? by = null) => Scenario.ThenNotDeadLettered<T>(by);

    /// <inheritdoc cref="WolverineScenario.ThenHandled{T}(string)" />
    public void ThenHandled<T>(string by) => Scenario.ThenHandled<T>(by);

    /// <inheritdoc cref="WolverineScenario.ThenNotHandled{T}(string)" />
    public void ThenNotHandled<T>(string by) => Scenario.ThenNotHandled<T>(by);

    /// <inheritdoc cref="WolverineScenario.ThenAttempted{T}" />
    public void ThenAttempted<T>(int count, string? by = null) => Scenario.ThenAttempted<T>(count, by);

    /// <inheritdoc cref="WolverineScenario.ThenGroupedBy{T}" />
    public void ThenGroupedBy<T>(object groupId) => Scenario.ThenGroupedBy<T>(groupId);

    // ---- sagas ------------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaHandled{TSaga, TMessage}" />
    public void ThenTheSagaHandled<TSaga, TMessage>(object sagaId) where TSaga : Saga
        => Scenario.ThenTheSagaHandled<TSaga, TMessage>(sagaId);

    /// <inheritdoc cref="WolverineScenario.GivenSaga{TSaga}(object)" />
    public Task GivenSaga<TSaga>(object saga) where TSaga : Saga => Scenario.GivenSaga<TSaga>(saga);

    /// <inheritdoc cref="WolverineScenario.ThenSaga{TSaga}(object, object)" />
    public Task<TSaga> ThenSaga<TSaga>(object sagaId, object expected) where TSaga : Saga
        => Scenario.ThenSaga<TSaga>(sagaId, expected);

    /// <inheritdoc cref="WolverineScenario.ThenSaga{TSaga}(object)" />
    public Task<TSaga> ThenSaga<TSaga>(object sagaId) where TSaga : Saga => Scenario.ThenSaga<TSaga>(sagaId);

    /// <inheritdoc cref="WolverineScenario.ThenNoSaga{TSaga}(object)" />
    public Task ThenNoSaga<TSaga>(object sagaId) where TSaga : Saga => Scenario.ThenNoSaga<TSaga>(sagaId);

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
