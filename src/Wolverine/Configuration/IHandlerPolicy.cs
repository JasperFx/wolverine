using JasperFx.CodeGeneration;
using Lamar;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

#region sample_IHandlerPolicy

/// <summary>
///     Use to apply your own conventions or policies to message handlers
/// </summary>
public interface IHandlerPolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="graph"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying Lamar Container</param>
    void Apply(HandlerGraph graph, GenerationRules rules, IContainer container);
}

#endregion