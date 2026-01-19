using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// Variable source that provides variables created by middleware frames
/// so they can be found during service dependency resolution.
/// This enables services with constructor dependencies on middleware-created types
/// to be properly resolved.
/// </summary>
public class MiddlewareVariableSource : IVariableSource
{
    private readonly IReadOnlyList<Variable> _middlewareVariables;

    public MiddlewareVariableSource(IEnumerable<Frame> middlewareFrames)
    {
        _middlewareVariables = middlewareFrames
            .SelectMany(f => f.Creates)
            .ToList();
    }

    public bool Matches(Type type)
    {
        return _middlewareVariables.Any(v => v.VariableType == type);
    }

    public Variable Create(Type type)
    {
        return _middlewareVariables.First(v => v.VariableType == type);
    }
}
