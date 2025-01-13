using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence;

internal class EntityVariable : Variable
{
    public EntityVariable(Variable sideEffect) : base(sideEffect.VariableType.GetGenericArguments()[0], $"{sideEffect.Usage}.Entity")
    {
    }
}