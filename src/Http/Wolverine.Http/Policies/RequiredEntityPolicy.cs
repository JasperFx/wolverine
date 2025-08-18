using System.ComponentModel.DataAnnotations;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime;

namespace Wolverine.Http.Policies;

internal class RequiredEntityPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            var requiredParameters = chain.Method.Method.GetParameters()
                .Where(x => x.HasAttribute<RequiredAttribute>() && x.ParameterType.IsClass).ToArray();

            if (requiredParameters.Length != 0)
            {
                chain.Metadata.Produces(404);

                foreach (var parameter in requiredParameters)
                {
                    var stopFrame = new SetStatusCodeAndReturnIfEntityIsNullFrame(parameter.ParameterType);
                    var loadFrame = chain.Middleware.FirstOrDefault(m => m.Creates.Any(x => x.VariableType == parameter.ParameterType));
                    if (loadFrame is not null)
                    {
                        chain.Middleware.Insert(chain.Middleware.IndexOf(loadFrame) + 1, stopFrame);
                    }
                    else
                    {
                        chain.Middleware.Add(stopFrame);
                    }
                }
            }
        }
    }
}