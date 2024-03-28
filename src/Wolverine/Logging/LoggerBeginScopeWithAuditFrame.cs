using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Logging;

internal class LoggerBeginScopeWithAuditFrame : SyncFrame
{
    private readonly IReadOnlyList<AuditedMember> _members;
    private readonly Type? _inputType;
    private Variable? _logger;
    private Variable? _withAudit;

    public LoggerBeginScopeWithAuditFrame(IChain chain)
    {
        _members = chain.AuditedMembers.AsReadOnly();
        _inputType = chain.InputType()!;
    }
    public LoggerBeginScopeWithAuditFrame(IReadOnlyList<AuditedMember> members, Variable variable)
    {
        _members = members;
        _withAudit = variable;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;

        if (_inputType is null) yield break;
        _withAudit = chain.FindVariable(_inputType);
        yield return _withAudit;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_members.Count > 0)
        {
            writer.WriteComment("Adding audited members to log context");
            writer.Write(
                $"using var disposable_{Guid.NewGuid().ToString().Replace("-", "_")} = {_logger!.Usage}.{nameof(ILogger.BeginScope)}"
                + $"(new {typeof(Dictionary<string, object>).FullNameInCode()}(){{{string.Join(", ", _members.Select(member => $"{{\"{member.MemberName}\", {_withAudit!.Usage}.{member.Member.Name}}}"))}}});");
        }

        Next?.GenerateCode(method, writer);
    }

}