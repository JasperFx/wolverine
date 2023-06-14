using System.Reflection;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Serialization;

internal class Forwarders
{
    public Dictionary<Type, Type> Relationships { get; } = new();

    public void Add(Type type)
    {
        var forwardedType = type
            .FindInterfaceThatCloses(typeof(IForwardsTo<>))!
            .GetGenericArguments()
            .Single();

        Relationships[type] = forwardedType;
    }

    public void FindForwards(Assembly assembly)
    {
        var candidates = assembly.ExportedTypes.Where(x => x.IsConcrete() && !x.IsOpenGeneric());
        foreach (var type in candidates.Where(t => t.Closes(typeof(IForwardsTo<>)))) Add(type);
    }
}