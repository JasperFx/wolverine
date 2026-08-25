using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Util;

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

        // The message can come straight from an [Entity(MissingMessage = "...")], so it is only ever
        // emitted as an escaped literal — see ToStringLiteral for why Constant.For will not do.
        var literal = Message.ToStringLiteral();
        var exceptionType = typeof(RequiredDataMissingException).FullNameInCode();

        if (Identity != null && Message.Contains("{0}"))
        {
            writer.Write($"throw new {exceptionType}(string.Format({literal}, {Identity.Usage}));");
        }
        else if (Identity != null && Message.Contains("{Id}"))
        {
            var toStringExpression = Identity.VariableType.IsValueType
                ? $"{Identity.Usage}.ToString()"
                : $"{Identity.Usage}?.ToString() ?? \"\"";

            writer.Write($"throw new {exceptionType}({literal}.Replace(\"{{Id}}\", {toStringExpression}));");
        }
        else
        {
            // Either there is no identity to substitute — a loader-backed [Entity] — or the message
            // never asked for one, so it goes out as it stands.
            writer.Write($"throw new {exceptionType}({literal});");
        }

        writer.FinishBlock();
        Next?.GenerateCode(method, writer);
    }
}