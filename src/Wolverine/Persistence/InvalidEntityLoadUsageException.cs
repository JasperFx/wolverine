using System.Reflection;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

public class InvalidEntityLoadUsageException : Exception
{
    public InvalidEntityLoadUsageException(EntityAttribute att, ParameterInfo parameter) : base($"Unable to determine a value variable named '{att.ArgumentName}' and source {att.ValueSource} to load an entity of type {parameter.ParameterType.FullNameInCode()} for parameter {parameter.Name}")
    {
    }
}