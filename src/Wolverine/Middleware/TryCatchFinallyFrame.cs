using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Middleware;

/// <summary>
/// A composite frame that wraps the rest of the chain in a try/catch/finally block.
/// Catch blocks are ordered by exception type specificity (most derived first).
/// Finally blocks execute cleanup code regardless of exceptions.
/// </summary>
public class TryCatchFinallyFrame : Frame
{
    private readonly List<CatchBlock> _catchBlocks = [];
    private readonly List<Frame> _finallyBlocks = [];

    public TryCatchFinallyFrame() : base(true)
    {
    }

    public IReadOnlyList<CatchBlock> CatchBlocks => _catchBlocks;
    public IReadOnlyList<Frame> FinallyBlocks => _finallyBlocks;

    public void AddCatchBlock(Type exceptionType, Frame[] frames)
    {
        _catchBlocks.Add(new CatchBlock(exceptionType, frames));
        SortCatchBlocks();
    }

    public void AddFinallyBlock(Frame frame)
    {
        _finallyBlocks.Add(frame);
    }

    private void SortCatchBlocks()
    {
        // Sort by inheritance depth descending — most specific exception types first
        _catchBlocks.Sort((a, b) => InheritanceDepth(b.ExceptionType).CompareTo(InheritanceDepth(a.ExceptionType)));
    }

    internal static int InheritanceDepth(Type type)
    {
        var depth = 0;
        var current = type;
        while (current != null && current != typeof(object))
        {
            depth++;
            current = current.BaseType;
        }
        return depth;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        foreach (var catchBlock in _catchBlocks)
        {
            var exceptionVarName = catchBlock.ExceptionVariable.Usage;
            writer.Write($"BLOCK:catch ({catchBlock.ExceptionType.FullNameInCode()} {exceptionVarName})");

            // Chain the catch frames together
            if (catchBlock.Frames.Length > 1)
            {
                for (var i = 0; i < catchBlock.Frames.Length - 1; i++)
                {
                    catchBlock.Frames[i].Next = catchBlock.Frames[i + 1];
                }
            }

            // Null out the last frame's Next to prevent it from continuing the chain
            if (catchBlock.Frames.Length > 0)
            {
                catchBlock.Frames[^1].Next = null;
                catchBlock.Frames[0].GenerateCode(method, writer);
            }

            writer.FinishBlock();
        }

        if (_finallyBlocks.Count > 0)
        {
            writer.Write("BLOCK:finally");

            if (_finallyBlocks.Count > 1)
            {
                for (var i = 0; i < _finallyBlocks.Count - 1; i++)
                {
                    _finallyBlocks[i].Next = _finallyBlocks[i + 1];
                }
            }

            _finallyBlocks[^1].Next = null;
            _finallyBlocks[0].GenerateCode(method, writer);

            writer.FinishBlock();
        }
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // For catch blocks, we need to filter out the exception type variables
        // since those come from the catch clause, not from the method variables chain.
        // We wrap the chain to provide the exception variable when asked.
        foreach (var catchBlock in _catchBlocks)
        {
            var wrappedChain = new CatchBlockMethodVariables(chain, catchBlock.ExceptionVariable);
            foreach (var frame in catchBlock.Frames)
            {
                foreach (var variable in frame.FindVariables(wrappedChain))
                {
                    // Skip the exception variable itself — it's provided by the catch clause
                    if (variable == catchBlock.ExceptionVariable) continue;
                    yield return variable;
                }
            }
        }

        foreach (var finallyFrame in _finallyBlocks)
        {
            foreach (var variable in finallyFrame.FindVariables(chain))
            {
                yield return variable;
            }
        }
    }
}

/// <summary>
/// Wraps IMethodVariables to provide the exception variable for catch block frames
/// </summary>
internal class CatchBlockMethodVariables : IMethodVariables
{
    private readonly IMethodVariables _inner;
    private readonly Variable _exceptionVariable;

    public CatchBlockMethodVariables(IMethodVariables inner, Variable exceptionVariable)
    {
        _inner = inner;
        _exceptionVariable = exceptionVariable;
    }

    public Variable FindVariable(Type type)
    {
        if (type == _exceptionVariable.VariableType || type.IsAssignableFrom(_exceptionVariable.VariableType))
        {
            return _exceptionVariable;
        }
        return _inner.FindVariable(type);
    }

    public Variable FindVariable(System.Reflection.ParameterInfo parameter)
    {
        if (parameter.ParameterType == _exceptionVariable.VariableType ||
            parameter.ParameterType.IsAssignableFrom(_exceptionVariable.VariableType))
        {
            return _exceptionVariable;
        }
        return _inner.FindVariable(parameter);
    }

    public Variable FindVariableByName(Type dependency, string name)
    {
        if (name == _exceptionVariable.Usage &&
            (dependency == _exceptionVariable.VariableType || dependency.IsAssignableFrom(_exceptionVariable.VariableType)))
        {
            return _exceptionVariable;
        }
        return _inner.FindVariableByName(dependency, name);
    }

    public bool TryFindVariableByName(Type dependency, string name, out Variable? variable)
    {
        if (name == _exceptionVariable.Usage &&
            (dependency == _exceptionVariable.VariableType || dependency.IsAssignableFrom(_exceptionVariable.VariableType)))
        {
            variable = _exceptionVariable;
            return true;
        }
        return _inner.TryFindVariableByName(dependency, name, out variable);
    }

    public Variable? TryFindVariable(Type type, VariableSource source)
    {
        if (type == _exceptionVariable.VariableType || type.IsAssignableFrom(_exceptionVariable.VariableType))
        {
            return _exceptionVariable;
        }
        return _inner.TryFindVariable(type, source);
    }

}

public class CatchBlock
{
    public CatchBlock(Type exceptionType, Frame[] frames)
    {
        ExceptionType = exceptionType;
        Frames = frames;
        ExceptionVariable = new Variable(exceptionType, DefaultExceptionVariableName(exceptionType));
    }

    public Type ExceptionType { get; }
    public Frame[] Frames { get; }
    public Variable ExceptionVariable { get; }

    internal static string DefaultExceptionVariableName(Type exceptionType)
    {
        // Use a simple, readable variable name based on the exception type
        var name = exceptionType.Name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
