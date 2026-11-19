using Wolverine.Persistence.Sagas;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// GH-5415. The event-sourced saga types the event stores keep, and how each one is kept
    /// </summary>
    internal EventSourcedSagaRegistration EventSourcedSagas { get; } = new();
}
