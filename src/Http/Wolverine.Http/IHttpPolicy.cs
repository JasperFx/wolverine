using JasperFx;
using JasperFx.CodeGeneration;
using Wolverine.Runtime;

namespace Wolverine.Http;

#region sample_IHttpPolicy

/// <summary>
///     Use to apply your own conventions or policies to HTTP endpoint handlers
/// </summary>
public interface IHttpPolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="chains"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying IoC Container</param>
    void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container);
}

#endregion

internal class LambdaHttpPolicy : IHttpPolicy
{
    private readonly Action<HttpChain, GenerationRules, IServiceContainer> _action;

    public LambdaHttpPolicy(Action<HttpChain, GenerationRules, IServiceContainer> action)
    {
        _action = action;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains) _action(chain, rules, container);
    }
}