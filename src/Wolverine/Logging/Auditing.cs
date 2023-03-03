using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Configuration;

namespace Wolverine.Logging;

/// <summary>
/// Configuration item for a member on the message type that should be audited
/// in instrumentation
/// </summary>
/// <param name="Member"></param>
/// <param name="Heading"></param>
public record AuditedMember(MemberInfo Member, string Heading);

internal class AuditMembersPolicy<T> : IChainPolicy
{
    private readonly MemberInfo[] _members;

    public AuditMembersPolicy(MemberInfo[] members)
    {
        _members = members;
    }

    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains.Where(x => x.InputType().CanBeCastTo<T>()))
        {
            foreach (var member in _members)
            {
                chain.Audit(member);
            }
        }
    }
}

