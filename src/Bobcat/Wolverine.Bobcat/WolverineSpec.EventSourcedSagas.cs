using JasperFx.Events.Projections;

namespace Wolverine.Bobcat;

// GH-5415: the vocabulary of WolverineScenario.EventSourcedSagas.cs, as members of the spec base class.
public abstract partial class WolverineSpec
{
    // ---- history ------------------------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhenTheSagaHistoryIsRead{TSaga}" />
    public Task WhenTheSagaHistoryIsRead<TSaga>(object sagaId, int page = 1, int pageSize = 50) where TSaga : Saga
        => Scenario.WhenTheSagaHistoryIsRead<TSaga>(sagaId, page, pageSize);

    /// <inheritdoc cref="WolverineScenario.WhenTheSagaIsReadAtVersion{TSaga}" />
    public Task WhenTheSagaIsReadAtVersion<TSaga>(object sagaId, long version) where TSaga : Saga
        => Scenario.WhenTheSagaIsReadAtVersion<TSaga>(sagaId, version);

    /// <inheritdoc cref="WolverineScenario.WhenTheSagaIsReadAsOf{TSaga}" />
    public Task WhenTheSagaIsReadAsOf<TSaga>(object sagaId, DateTimeOffset timestamp, string described) where TSaga : Saga
        => Scenario.WhenTheSagaIsReadAsOf<TSaga>(sagaId, timestamp, described);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaHistoryIs" />
    public void ThenTheSagaHistoryIs(params Type[] eventTypes) => Scenario.ThenTheSagaHistoryIs(eventTypes);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaHistoryHasInAll" />
    public void ThenTheSagaHistoryHasInAll(long count) => Scenario.ThenTheSagaHistoryHasInAll(count);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaWas" />
    public void ThenTheSagaWas(object expected) => Scenario.ThenTheSagaWas(expected);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaSupportsHistory{TSaga}" />
    public Task ThenTheSagaSupportsHistory<TSaga>() where TSaga : Saga => Scenario.ThenTheSagaSupportsHistory<TSaga>();

    // ---- completion, the snapshot and the saga explorer --------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaIsCompleted{TSaga}" />
    public Task ThenTheSagaIsCompleted<TSaga>(object sagaId) where TSaga : Saga => Scenario.ThenTheSagaIsCompleted<TSaga>(sagaId);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaIsStillOpen{TSaga}" />
    public Task ThenTheSagaIsStillOpen<TSaga>(object sagaId) where TSaga : Saga => Scenario.ThenTheSagaIsStillOpen<TSaga>(sagaId);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaSnapshot{TSaga}" />
    public Task ThenTheSagaSnapshot<TSaga>(object sagaId, object expected) where TSaga : Saga
        => Scenario.ThenTheSagaSnapshot<TSaga>(sagaId, expected);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaIsKeptAs{TSaga}" />
    public Task ThenTheSagaIsKeptAs<TSaga>(ProjectionLifecycle lifecycle) where TSaga : Saga
        => Scenario.ThenTheSagaIsKeptAs<TSaga>(lifecycle);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaExplorerShows{TSaga}" />
    public Task ThenTheSagaExplorerShows<TSaga>(object sagaId, object expected) where TSaga : Saga
        => Scenario.ThenTheSagaExplorerShows<TSaga>(sagaId, expected);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaExplorerCanSearch{TSaga}" />
    public void ThenTheSagaExplorerCanSearch<TSaga>(bool can = true) where TSaga : Saga
        => Scenario.ThenTheSagaExplorerCanSearch<TSaga>(can);

    // ---- retention ----------------------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhenCompletedSagasAreSwept{TSaga}" />
    public Task WhenCompletedSagasAreSwept<TSaga>(TimeSpan later) where TSaga : Saga
        => Scenario.WhenCompletedSagasAreSwept<TSaga>(later);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaStreamIsArchived{TSaga}" />
    public Task ThenTheSagaStreamIsArchived<TSaga>(object sagaId) where TSaga : Saga
        => Scenario.ThenTheSagaStreamIsArchived<TSaga>(sagaId);

    /// <inheritdoc cref="WolverineScenario.ThenTheSagaStreamIsNotArchived{TSaga}" />
    public Task ThenTheSagaStreamIsNotArchived<TSaga>(object sagaId) where TSaga : Saga
        => Scenario.ThenTheSagaStreamIsNotArchived<TSaga>(sagaId);

    // ---- a service that refuses to start ------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhenAServiceStarts" />
    public Task WhenAServiceStarts(string description, Action<WolverineOptions> configure)
        => Scenario.WhenAServiceStarts(description, configure);

    /// <inheritdoc cref="WolverineScenario.ThenTheServiceStarted" />
    public void ThenTheServiceStarted() => Scenario.ThenTheServiceStarted();

    /// <inheritdoc cref="WolverineScenario.ThenTheServiceRefusedToStart" />
    public void ThenTheServiceRefusedToStart(string reason, params object[] mentioning)
        => Scenario.ThenTheServiceRefusedToStart(reason, mentioning);

    // ---- concurrency seams ---------------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.WhileActingOn{TAggregate}" />
    [global::Bobcat.EventModelRole(global::Bobcat.SliceRole.ArrangedEvent, nameof(concurrently))]
    public void WhileActingOn<TAggregate>(object id, params object[] concurrently) where TAggregate : class
        => Scenario.WhileActingOn<TAggregate>(id, concurrently);

    /// <inheritdoc cref="WolverineScenario.WhileCompensatingAnotherMessageAppends{TSaga}" />
    public void WhileCompensatingAnotherMessageAppends<TSaga>(object sagaId, object @event, bool onEveryAttempt = false)
        where TSaga : EventSourcedSaga
        => Scenario.WhileCompensatingAnotherMessageAppends<TSaga>(sagaId, @event, onEveryAttempt);

    // ---- dead letters -------------------------------------------------------------------------------------------

    /// <inheritdoc cref="WolverineScenario.ThenDeadLettered{T, TException}" />
    public void ThenDeadLettered<T, TException>(string? by = null) where TException : Exception
        => Scenario.ThenDeadLettered<T, TException>(by);
}
