using System.Reflection;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http;

public partial class HttpGraph
{
    private readonly List<IParameterStrategy> _strategies = new()
    {
        new HttpChainParameterAttributeStrategy(),
        new FromServicesParameterStrategy(),
        new MessageBusStrategy(),
        new HttpContextElements(),
        new RouteParameterStrategy(),
        new FromHeaderStrategy(),
        new QueryStringParameterStrategy(),
        new JsonBodyParameterStrategy()
    };

    internal void ApplyParameterMatching(HttpChain chain)
    {
        var parameters = chain.Method.Method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            if (!TryMatchParameter(chain, parameter, i))
            {
            }
        }
    }

    internal bool TryMatchParameter(HttpChain chain, ParameterInfo parameter, int i)
    {
        foreach (var strategy in _strategies)
        {
            if (strategy.TryMatch(chain, Container, parameter, out var variable))
            {
                if (variable != null)
                {
                    if (variable.Creator != null)
                    {
                        chain.Middleware.Add(variable.Creator);
                    }

                    chain.Method.Arguments[i] = variable;
                }

                return true;
            }
        }

        return false;
    }

    public void InsertParameterStrategy(IParameterStrategy strategy)
    {
        _strategies.Insert(0, strategy);
    }
}