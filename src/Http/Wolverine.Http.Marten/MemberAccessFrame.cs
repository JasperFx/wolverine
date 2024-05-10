using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.Marten;

internal class MemberAccessFrame : SyncFrame
{
    private readonly Type _targetType;
    private readonly MemberInfo _member;
    private Variable _parent;
    public Variable Variable { get; }

    public MemberAccessFrame(Type targetType, MemberInfo member, string name)
    {
        _targetType = targetType;
        _member = member;
        Variable = new Variable(member.GetMemberType(), name, this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = {_parent.Usage}.{_member.Name};");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _parent = chain.FindVariable(_targetType);
        yield return _parent;
    }
}