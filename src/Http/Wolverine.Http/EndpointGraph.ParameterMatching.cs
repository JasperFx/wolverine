using System.Reflection;

namespace Wolverine.Http;

public partial class EndpointGraph
{
    // TODO -- make this pluggable later???
    private readonly List<IParameterStrategy> _strategies = new();
    
    internal void ApplyMatching(EndpointChain chain)
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

    internal bool TryMatchParameter(EndpointChain chain, ParameterInfo parameter, int i)
    {
        foreach (var strategy in _strategies)
        {
            if (strategy.TryMatch(chain, Container, parameter, out var variable))
            {
                chain.Method.Arguments[i] = variable;
                return true;
            }
        }

        return false;
    }
}