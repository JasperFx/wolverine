using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime;

namespace Wolverine.Logging;

public class TenantIdLogContextFrame : SyncFrame
{
    public const string TenantIdContextName = "TenantId";
    private Variable _messageContext = null!;
    private Variable _loggingContext = null!;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _messageContext = chain.FindVariable(typeof(MessageContext));
        yield return _messageContext;
        _loggingContext = chain.FindVariable(typeof(LoggingContext));
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{_loggingContext.Usage}.{nameof(LoggingContext.Add)}(\"{TenantIdContextName}\", {_messageContext.Usage}.{nameof(MessageContext.TenantId)} ?? \"[NotSet]\");");
        Next?.GenerateCode(method, writer);
    }
}
