namespace Wolverine;

/// <summary>
/// Exception thrown when Wolverine is used before the host is started
/// </summary>
public class WolverineHasNotStartedException : Exception
{
    public WolverineHasNotStartedException() : base("Wolverine cannot function until the underlying IHost has been started. This can happen if IHost.Build() is called but not IHost.Start()/StartAsync()?")
    {
    }
}