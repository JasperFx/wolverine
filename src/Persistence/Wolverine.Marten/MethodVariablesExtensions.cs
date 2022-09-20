using LamarCodeGeneration.Model;

namespace Wolverine.Marten;

internal static class MethodVariablesExtensions
{
    internal static bool IsUsingMartenPersistence(this IMethodVariables method)
    {
        return method.TryFindVariable(typeof(MartenBackedPersistenceMarker), VariableSource.NotServices) != null;
    }
}
