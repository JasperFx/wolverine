using System.Reflection;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http;

public partial class HttpGraph
{
    private readonly List<IParameterStrategy> _strategies = new()
    {
        new MessageBusStrategy(),
        new HttpContextElements(),
        new RouteParameterStrategy(),
        new QueryStringParameterStrategy(),
        new JsonBodyParameterStrategy()
    };
    
    internal void ApplyParameterMatching(HttpChain chain)
    {
        var parameters = chain.Method.Method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            if (!TryMatchParameter(chain, parameter, i))
            {
                return;
            }
        }
    }

    internal bool TryMatchParameter(HttpChain chain, ParameterInfo parameter, int i)
    {
        foreach (var strategy in _strategies)
        {
            if (strategy.TryMatch(chain, Container, parameter, out var variable))
            {
                if (variable.Creator != null)
                {
                    chain.Middleware.Add(variable.Creator);    
                }
                
                chain.Method.Arguments[i] = variable;
                return true;
            }
            
            
        }

        return false;
    }
}