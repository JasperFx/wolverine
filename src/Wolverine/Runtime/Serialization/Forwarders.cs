using System.Diagnostics.CodeAnalysis;
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

    [RequiresUnreferencedCode(
        "Walks Assembly.ExportedTypes looking for IForwardsTo<> implementations. Trimming may remove " +
        "message-forwarder types that are only reached reflectively here. Apps in TypeLoadMode.Static " +
        "have the forwards baked into pre-generated code and don't reach this path.")]
    public void FindForwards(Assembly assembly)
    {
        var candidates = assembly.ExportedTypes.Where(x => x.IsConcrete() && !x.IsOpenGeneric());
        foreach (var type in candidates.Where(t => t.Closes(typeof(IForwardsTo<>)))) Add(type);
    }
}