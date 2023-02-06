using System.Reflection;
using JasperFx.CodeGeneration.Model;
using Lamar;

namespace Wolverine.Http.CodeGen;


public interface IParameterStrategy
{
    bool TryMatch(EndpointChain chain, IContainer container, ParameterInfo parameter, out Variable variable);
}