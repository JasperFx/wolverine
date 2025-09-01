using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

/// <summary>
///     Generic middleware chain policy that spans messaging and HTTP endpoints
/// </summary>
public interface IChainPolicy : IWolverinePolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="chains"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying IoC Container</param>
    void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container);
}

internal class HandlerChainPolicy : IHandlerPolicy
{
    private readonly IChainPolicy _inner;

    public HandlerChainPolicy(IChainPolicy inner)
    {
        _inner = inner;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        _inner.Apply(chains, rules, container);
    }
}

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
    /// <param name="container">The application's underlying IoC Container</param>
    void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container);
}

#endregion
