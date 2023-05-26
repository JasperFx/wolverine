using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Logging;

internal class AuditToActivityFrame : SyncFrame
{
    private readonly Type _inputType;
    private readonly List<AuditedMember> _members;
    private Variable? _input;

    public AuditToActivityFrame(IChain chain)
    {
        _inputType = chain.InputType()!;
        _members = chain.AuditedMembers;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _input = chain.FindVariable(_inputType);
        yield return _input;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        foreach (var member in _members)
        {
            writer.WriteLine($"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{member.OpenTelemetryName}\", {_input.Usage}.{member.Member.Name});");
        }
        
        Next?.GenerateCode(method, writer);
    }
}