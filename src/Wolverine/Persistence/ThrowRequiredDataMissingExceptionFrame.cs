using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence;

internal class ThrowRequiredDataMissingExceptionFrame : SyncFrame
{
    public Variable Entity { get; }
    public Variable Identity { get; }
    public string Message { get; }
    
    public ThrowRequiredDataMissingExceptionFrame(Variable entity, Variable identity, string message)
    {
        Entity = entity;
        Identity = identity;
        Message = message;
        
        uses.Add(Entity);
        uses.Add(Identity);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Write ProblemDetails if this required object is null");
        writer.Write($"BLOCK:if ({Entity.Usage} == null)");

        if (Message.Contains("{0}"))
        {
            writer.Write($"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(string.Format(\"{Message}\", {Identity.Usage}));");
        }
        else if (Message.Contains("{Id}"))
        {
            writer.Write($"throw new {typeof(RequiredDataMissingException).FullNameInCode()}(\"{Message}\".Replace(\"{{Id}}\", {Identity.Usage}?.ToString() ?? \"\"));");
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