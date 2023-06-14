using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten.Events;

namespace Wolverine.Marten.Codegen;

internal class LoadAggregateFrame<T> : MethodCall where T : class
{
    private readonly AggregateHandlerAttribute _att;
    private Variable? _command;

    public LoadAggregateFrame(AggregateHandlerAttribute att) : base(typeof(IEventStore), FindMethod(att))
    {
        _att = att;
        CommentText = "Loading Marten aggregate";
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _command = chain.FindVariable(_att.CommandType!);
        yield return _command;

        Arguments[0] = new MemberAccessVariable(_command, _att.AggregateIdMember!);
        if (_att.LoadStyle == ConcurrencyStyle.Optimistic && _att.VersionMember != null)
        {
            Arguments[1] = new MemberAccessVariable(_command, _att.VersionMember);
        }

        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }

    internal static MethodInfo FindMethod(AggregateHandlerAttribute att)
    {
        var isGuidIdentified = att.AggregateIdMember!.GetMemberType() == typeof(Guid);

        if (att.LoadStyle == ConcurrencyStyle.Exclusive)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForExclusiveWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForExclusiveWriting<T>(string.Empty, default))!;
        }

        if (att.VersionMember == null)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(string.Empty, default))!;
        }

        return isGuidIdentified
            ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(Guid.Empty, long.MaxValue, default))!
            : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForWriting<T>(string.Empty, long.MaxValue, default))!;
    }
}