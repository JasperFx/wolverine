using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten.Events;

namespace Wolverine.Marten.Codegen;

internal class LoadAggregateFrame<T> : MethodCall where T : class
{
    private readonly AggregateHandling _att;

    public LoadAggregateFrame(AggregateHandling att) : base(typeof(IEventStoreOperations), FindMethod(att))
    {
        _att = att;
        CommentText = "Loading Marten aggregate";
        
        // Placeholder to keep the HTTP chains from trying to use QueryString
        Arguments[0] = Constant.ForString("temp");
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        Arguments[0] = _att.AggregateId;
        if (_att is { LoadStyle: ConcurrencyStyle.Optimistic, Version: not null })
        {
            Arguments[1] = _att.Version;
        }

        foreach (var variable in base.FindVariables(chain)) yield return variable;
    }

    internal static MethodInfo FindMethod(AggregateHandling att)
    {
        var isGuidIdentified = att.AggregateId!.VariableType == typeof(Guid);

        if (att.LoadStyle == ConcurrencyStyle.Exclusive)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchForExclusiveWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchForExclusiveWriting<T>(string.Empty, default))!;
        }

        if (att.Version == null)
        {
            return isGuidIdentified
                ? ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchForWriting<T>(Guid.Empty, default))!
                : ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchForWriting<T>(string.Empty, default))!;
        }

        return isGuidIdentified
            ? ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchForWriting<T>(Guid.Empty, long.MaxValue, default))!
            : ReflectionHelper.GetMethod<IEventStoreOperations>(x => x.FetchForWriting<T>(string.Empty, long.MaxValue, default))!;
    }
}