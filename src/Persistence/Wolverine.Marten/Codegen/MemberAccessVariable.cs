using System;
using System.Reflection;
using LamarCodeGeneration.Model;
using Oakton.Parsing;

namespace Wolverine.Marten.Codegen;

// TODO -- this should be in LamarCodeGeneration
public class MemberAccessVariable : Variable
{
    private readonly Variable _parent;
    private readonly MemberInfo _member;

    public MemberAccessVariable(Variable parent, MemberInfo member) : base(member.GetMemberType(), parent.Creator)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _member = member ?? throw new ArgumentNullException(nameof(member));
    }

    public override string Usage => $"{_parent?.Usage}.{_member?.Name}";
}
