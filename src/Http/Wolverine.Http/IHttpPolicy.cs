using JasperFx.CodeGeneration;
using Lamar;

namespace Wolverine.Http;

/// <summary>
/// Use to apply your own conventions or policies to HTTP endpoint handlers
/// </summary>
public interface IHttpPolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="chains"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying Lamar Container</param>
    void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container);
}

internal class LambdaHttpPolicy : IHttpPolicy
{
    private readonly Action<HttpChain, GenerationRules, IContainer> _action;

    public LambdaHttpPolicy(Action<HttpChain, GenerationRules, IContainer> action)
    {
        _action = action;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains)
        {
            _action(chain, rules, container);
        }
    }
}