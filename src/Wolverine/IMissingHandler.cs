using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Wolverine;

#region sample_IMissingHandler

/// <summary>
///     Hook interface to receive notifications of envelopes received
///     that do not match any known handlers within the system
/// </summary>
public interface IMissingHandler
{
    /// <summary>
    ///     Executes for unhandled envelopes
    /// </summary>
    /// <param name="context"></param>
    /// <param name="root"></param>
    /// <returns></returns>
    ValueTask HandleAsync(IEnvelopeLifecycle context, IWolverineRuntime root);
}

#endregion