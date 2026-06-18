using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Logging;

public class AuditToActivityFrame : SyncFrame
{
    private readonly Type _inputType;
    private readonly List<AuditedMember> _members;
    private Variable? _input;

    public AuditToActivityFrame(IChain chain) : this(chain, null)
    {
    }

    /// <param name="inputType">
    /// The type of the variable the audited members are read from. Defaults to <see cref="IChain.InputType"/>,
    /// but callers can override it when the audited members live on a different bound variable than the
    /// chain's request body — e.g. an [AsParameters] container whose [FromBody] member has overwritten
    /// the request type. See GH-3135.
    /// </param>
    public AuditToActivityFrame(IChain chain, Type? inputType)
    {
        _inputType = inputType ?? chain.InputType()!;
        _members = chain.AuditedMembers;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _input = chain.FindVariable(_inputType);
        yield return _input;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Application-specific Open Telemetry auditing");
        foreach (var member in _members)
        {
            writer.WriteLine(
                $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.{nameof(Activity.SetTag)}(\"{member.OpenTelemetryName}\", {_input!.Usage}.{member.Member.Name});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Application-specific Open Telemetry auditing");

        // F# has no null-conditional operator, and SetTag returns the Activity (discarded), so guard
        // Activity.Current once and pipe each tagging call to `ignore`. Skip the guard entirely when
        // there are no audited members so the `if` body is never empty.
        if (_members.Count > 0)
        {
            var current = $"{typeof(Activity).FSharpName()}.{nameof(Activity.Current)}";
            writer.Write($"BLOCK:if not (isNull {current}) then");
            foreach (var member in _members)
            {
                writer.Write(
                    $"{current}.{nameof(Activity.SetTag)}(\"{member.OpenTelemetryName}\", {_input!.FSharpUsage}.{member.Member.Name}) |> ignore");
            }

            writer.FinishBlock();
        }

        Next?.GenerateFSharpCode(method, writer);
    }
}