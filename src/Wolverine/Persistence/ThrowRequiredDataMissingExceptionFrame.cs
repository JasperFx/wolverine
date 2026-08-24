using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

internal class ThrowRequiredDataMissingExceptionFrame : SyncFrame
{
    public Variable Entity { get; }

    /// <summary>
    /// Null when the entity was not addressed by a single identity value — a loader-backed
    /// <c>[Entity]</c>, for instance. The message is then used as it stands.
    /// </summary>
    public Variable? Identity { get; }

    public string Message { get; }
    
    public ThrowRequiredDataMissingExceptionFrame(Variable entity, Variable? identity, string message)
    {
        Entity = entity;
        Identity = identity;
        Message = message;
        
        uses.Add(Entity);
        if (Identity != null)
        {
            uses.Add(Identity);
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Write ProblemDetails if this required object is null");
        writer.Write($"BLOCK:if ({Entity.Usage} == null)");

        if (Identity == null)
        {
            // Nothing to substitute into the message, so write it out as it stands.
            var literal = Constant.For(Message);
            writer.Write($"throw new {typeof(RequiredDataMissingException).FullNameInCode()}({literal.Usage});");
        }
        else if (Message.Contains("{0}"))
        {
            writer.Write($"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(string.Format(\"{Message}\", {Identity.Usage}));");
        }
        else if (Message.Contains("{Id}"))
        {
            var toStringExpression = Identity.VariableType.IsValueType
                ? $"{Identity.Usage}.ToString()"
                : $"{Identity.Usage}?.ToString() ?? \"\"";

            writer.Write($"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(\"{Message}\".Replace(\"{{Id}}\", {toStringExpression}));");
        }
        else
        {
            var constant = Constant.For(Message);
            writer.Write($"throw new {typeof(RequiredDataMissingException).FullNameInCode()}({constant.Usage});");
        }

        writer.FinishBlock();
        Next?.GenerateCode(method, writer);
    }
}