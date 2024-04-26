using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http;

public partial class HttpGraph
{
    private readonly List<IParameterStrategy> _strategies =
    [
        new FromFileStrategy(),
        new HttpChainParameterAttributeStrategy(),
        new FromServicesParameterStrategy(),
        new MessageBusStrategy(),
        new HttpContextElements(),
        new RouteParameterStrategy(),
        new FromHeaderStrategy(),
        new QueryStringParameterStrategy(),
        new JsonBodyParameterStrategy()
    ];

    internal void ApplyParameterMatching(HttpChain chain)
    {
        var methodCall = chain.Method;
        ApplyParameterMatching(chain, methodCall);
    }

    internal void ApplyParameterMatching(HttpChain chain, MethodCall methodCall)
    {
        var parameters = methodCall.Method.GetParameters();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            
            // Do *not* do anything if the argument variable has already
            // been resolved
            if (methodCall.Arguments[i] != null) continue;

            if (!TryMatchParameter(chain, methodCall, parameter, i))
            {
            }
        }
    }

    internal bool TryMatchParameter(HttpChain chain, MethodCall methodCall, ParameterInfo parameter, int i)
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

                    methodCall.Arguments[i] = variable;
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