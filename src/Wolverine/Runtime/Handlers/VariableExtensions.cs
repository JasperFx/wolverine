using LamarCodeGeneration.Model;

namespace Wolverine.Runtime.Handlers;

internal static class VariableExtensions
{
    public static bool ShouldBeCascaded(this Variable variable)
    {
        return !variable.Properties.ContainsKey(HandlerChain.NotCascading);
    }

    public static void MarkAsNotCascaded(this Variable variable)
    {
        variable.Properties[HandlerChain.NotCascading] = true;
    }
}
