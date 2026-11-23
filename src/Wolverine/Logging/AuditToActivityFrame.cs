using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Logging;

public class AuditToActivityFrame : SyncFrame
{
    private readonly Type _inputType;
    private readonly MemberInfo? _bodyMember;
    private readonly List<AuditedMember> _members;
    private Variable? _input;
    private MessageActivityVariable? _activity;

    public AuditToActivityFrame(IChain chain) : this(chain, null)
    {
    }

    /// <param name="inputType">
    /// The type of the variable the audited members are read from. Defaults to <see cref="IChain.InputType"/>,
    /// but callers can override it when the audited members live on a different bound variable than the
    /// chain's request body — e.g. an [AsParameters] container whose [FromBody] member has overwritten
    /// the request type. See GH-3135.
    /// </param>
    public AuditToActivityFrame(IChain chain, Type? inputType) : this(chain, inputType, null)
    {
    }

    /// <param name="bodyMember">
    /// The member of <paramref name="inputType"/> that holds the request body, when audited members are declared on
    /// the body's type rather than on <paramref name="inputType"/> itself — e.g. an [AsParameters] container whose
    /// [FromBody] member has no standalone variable. Those members are read through it.
    /// </param>
    public AuditToActivityFrame(IChain chain, Type? inputType, MemberInfo? bodyMember)
    {
        _inputType = inputType ?? chain.InputType()!;
        _bodyMember = bodyMember;
        _members = chain.AuditedMembers;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _input = chain.FindVariable(_inputType);
        yield return _input;

        // GH-5310: tag the message's own span rather than whatever Activity.Current has become.
        _activity = MessageActivityVariable.TryFind(chain);
        foreach (var variable in MessageActivityVariable.Uses(_activity)) yield return variable;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Application-specific Open Telemetry auditing");
        var activity = MessageActivityVariable.UsageFor(_activity);
        foreach (var member in _members)
        {
            writer.WriteLine(
                $"{activity}?.{nameof(Activity.SetTag)}(\"{member.OpenTelemetryName}\", {usageOf(member, _input!.Usage, "?.")});");
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
            var current = MessageActivityVariable.FSharpUsageFor(_activity);
            writer.Write($"BLOCK:if not (isNull {current}) then");
            foreach (var member in _members)
            {
                writer.Write(
                    $"{current}.{nameof(Activity.SetTag)}(\"{member.OpenTelemetryName}\", {usageOf(member, _input!.FSharpUsage, ".")}) |> ignore");
            }

            writer.FinishBlock();
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    // A member of the body's type is read through the body member, which may be null when the body is optional
    private string usageOf(AuditedMember member, string input, string bodyAccess)
    {
        if (_bodyMember == null || member.Member.DeclaringType!.IsAssignableFrom(_inputType))
        {
            return $"{input}.{member.Member.Name}";
        }

        var access = _bodyMember.GetMemberType()!.IsValueType && !_bodyMember.GetMemberType()!.IsNullable() ? "." : bodyAccess;
        return $"{input}.{_bodyMember.Name}{access}{member.Member.Name}";
    }
}