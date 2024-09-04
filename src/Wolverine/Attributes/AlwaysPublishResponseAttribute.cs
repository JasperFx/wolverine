using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
/// Decorate any handler method or class if you always want any response
/// (the "T" in IMessageBus.InvokeAsync<T>()) to be *also* published as a
/// message in addition to being the response object
/// </summary>
public class AlwaysPublishResponseAttribute : ModifyHandlerChainAttribute
{
    public override void Modify(HandlerChain chain, GenerationRules rules)
    {
        chain.Middleware.Add(new AlwaysPublishResponseFrame());
    }
}

internal class AlwaysPublishResponseFrame : SyncFrame
{
    private Variable _envelope;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Always publish the response as a message due to the [AlwaysPublishResponse] usage");
        writer.WriteLine($"{_envelope.Usage}.{nameof(Envelope.DoNotCascadeResponse)} = false;");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }
}