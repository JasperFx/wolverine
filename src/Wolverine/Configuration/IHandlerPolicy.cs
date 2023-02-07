using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

#region sample_IHandlerPolicy

/// <summary>
///     Use to apply your own conventions or policies to message handlers
/// </summary>
public interface IHandlerPolicy : IWolverinePolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="chains"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying Lamar Container</param>
    void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IContainer container);
}

#endregion