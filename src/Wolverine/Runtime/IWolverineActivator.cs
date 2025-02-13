namespace Wolverine.Runtime;

/// <summary>
/// Hook to enable taking some action against the IWolverineRuntime
/// of an application very early on in bootstrapping an application.
/// Originally built to register trackers
/// </summary>
public interface IWolverineActivator
{
    void Apply(IWolverineRuntime runtime);
}