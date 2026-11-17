using System.Reflection;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

/// <summary>
///     A member of the incoming message, as <see cref="HandlerChain.TryFindVariable" /> hands it to middleware
///     (an identity for <c>[DocumentExists&lt;T&gt;]</c>, an aggregate id, ...). JasperFx's
///     <see cref="MessageMemberVariable" /> only knows the C# cast, <c>((T)context.Envelope.Message).Member</c>,
///     and falls back to it for F#, where it does not compile. This adds the F# downcast.
/// </summary>
internal class FSharpMessageMemberVariable : MessageMemberVariable
{
    private readonly Type _messageType;

    public FSharpMessageMemberVariable(MemberInfo member, Type messageType) : base(member, messageType)
    {
        _messageType = messageType;
    }

    public override string FSharpUsage =>
        $"(context.{nameof(MessageContext.Envelope)}.{nameof(Envelope.Message)} :?> {_messageType.FSharpName()}).{Member.Name}";
}
