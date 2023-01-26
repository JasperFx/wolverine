using System.Reflection;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Http;


public interface IParameterStrategy
{
    bool TryMatch(EndpointChain chain, IContainer container, ParameterInfo parameter, out Variable variable);
}