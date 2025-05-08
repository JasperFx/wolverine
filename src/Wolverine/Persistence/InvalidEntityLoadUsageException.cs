using System.Reflection;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Persistence;

public class InvalidEntityLoadUsageException : Exception
{
    public InvalidEntityLoadUsageException(WolverineParameterAttribute att, ParameterInfo parameter) : base($"Unable to determine a value variable named '{att.ArgumentName}' and source {att.ValueSource} to load an entity of type {parameter.ParameterType.FullNameInCode()} for parameter {parameter.Name}")
    {
    }
}