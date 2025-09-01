using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Logging;

internal class AuditMembersPolicy<T> : IChainPolicy
{
    private readonly MemberInfo[] _members;

    public AuditMembersPolicy(MemberInfo[] members)
    {
        _members = members;
    }

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.InputType().CanBeCastTo<T>()))
        {
            foreach (var member in _members) chain.Audit(member);
        }
    }
}