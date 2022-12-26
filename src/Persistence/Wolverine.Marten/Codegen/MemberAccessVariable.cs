using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Marten.Codegen;

// TODO -- this should be in JasperFx.CodeGeneration
internal class MemberAccessVariable : Variable
{
    private readonly MemberInfo _member;
    private readonly Variable _parent;

    public MemberAccessVariable(Variable parent, MemberInfo member) : base(member.GetMemberType(), parent.Creator)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _member = member ?? throw new ArgumentNullException(nameof(member));
    }

    public override string Usage => $"{_parent?.Usage}.{_member?.Name}";
}