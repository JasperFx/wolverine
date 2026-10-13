using System.Reflection;
using JasperFx;
using Wolverine;

namespace CoreTests.Configuration;

/// <summary>
///     Snapshots the two process-wide application assembly pins (Wolverine's and JasperFx's) and puts them back on
///     dispose. Whichever host registers first in a test process pins the application assembly for every later
///     host, so a test that registers Wolverine from another assembly -- or that clears the pins to act as the
///     first host -- must not leave its pin behind, or the rest of the suite scans the wrong assembly for
///     handlers depending on test order.
/// </summary>
internal sealed class ProcessWideApplicationAssemblyPin : IDisposable
{
    private readonly Assembly? _jasperfx = JasperFxOptions.RememberedApplicationAssembly;
    private readonly Assembly? _wolverine = WolverineOptions.RememberedApplicationAssembly;

    /// <summary>
    ///     Clears both pins so the next host to register behaves as the first one in the process
    /// </summary>
    public static ProcessWideApplicationAssemblyPin AsFirstHost()
    {
        var pin = new ProcessWideApplicationAssemblyPin();
        JasperFxOptions.RememberedApplicationAssembly = null;
        WolverineOptions.RememberedApplicationAssembly = null;
        return pin;
    }

    public void Dispose()
    {
        JasperFxOptions.RememberedApplicationAssembly = _jasperfx;
        WolverineOptions.RememberedApplicationAssembly = _wolverine;
    }
}
