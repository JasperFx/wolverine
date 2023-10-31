using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten.Events;
using Wolverine.Marten;

namespace Wolverine.Http.Marten;

internal class LoadAggregateFrame<T> : MethodCall where T : class
{
    private readonly AggregateAttribute _att;

    public LoadAggregateFrame(AggregateAttribute att) : base(typeof(IEventStore), FindMethod(att))
    {
        _att = att;
        CommentText = "Loading Marten aggregate";
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Arguments[0] = _att.IdVariable;
        if (_att.LoadStyle == ConcurrencyStyle.Optimistic && _att.VersionVariable != null)
        {
            Arguments[1] = _att.VersionVariable;
        }

        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }

    internal static MethodInfo FindMethod(AggregateAttribute att)
    {
        var isGuidIdentified = att.IdVariable.VariableType == typeof(Guid);

        if (att.LoadStyle == ConcurrencyStyle.Exclusive)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForExclusiveWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStore>(x => x.FetchForExclusiveWriting<T>(string.Empty, default))!;
        }

        if (att.VersionVariable == null)
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