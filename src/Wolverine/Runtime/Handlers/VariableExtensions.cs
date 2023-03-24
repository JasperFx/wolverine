using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Runtime.Handlers;

internal static class VariableExtensions
{
    public static void UseReturnValueHandlingFrame(this Variable variable, Frame frame)
    {
        variable.Properties[HandlerChain.NotCascading] = frame;
    }

    public static bool TryGetReturnValueHandlingFrame(this Variable variable, out Frame frame)
    {
        if (variable.Properties.TryGetValue(HandlerChain.NotCascading, out var raw))
        {
            frame = (Frame)raw;
            return true;
        }
        
        frame = default!;
        return false;
    }
}