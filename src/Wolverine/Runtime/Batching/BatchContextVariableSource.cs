using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Makes <see cref="IBatchContext"/> injectable into any message handler, built from the active
/// <see cref="Envelope"/> the same way <c>TenantId</c> and the "now" clock are supplied. In a batched
/// handler the envelope is the assembled batch envelope, so the context reports the batch id and its
/// members; in an ordinary handler the membership is simply empty.
/// </summary>
internal class BatchContextVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IBatchContext);
    }

    public Variable Create(Type type)
    {
        return new BatchContextResolutionFrame().BatchContext;
    }
}

internal class BatchContextResolutionFrame : SyncFrame
{
    private Variable _envelope = null!;

    public BatchContextResolutionFrame()
    {
        BatchContext = new Variable(typeof(IBatchContext), "batchContext", this);
    }

    public Variable BatchContext { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine(
            $"var {BatchContext.Usage} = {typeof(BatchContext).FullNameInCode()}.For({_envelope.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
