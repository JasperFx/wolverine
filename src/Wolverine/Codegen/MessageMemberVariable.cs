using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Codegen;

/// <summary>
/// Represents a value for a member of the incoming message type
/// </summary>
public class MessageMemberVariable : Variable
{
    public MemberInfo Member { get; }

    public MessageMemberVariable(MemberInfo member, Type messageType) : base(member.GetRawMemberType(), $"(({messageType.FullNameInCode()})context.Envelope.Message).{member.Name}")
    {
        Member = member;
    }
}