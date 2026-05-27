using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Core.TypeScanning;

namespace Wolverine.Runtime.Serialization;

internal class Forwarders
{
    public Dictionary<Type, Type> Relationships { get; } = new();

    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "type is supplied by RegisterMessageForwarder<TForwarder> (DAM-annotated) or by the assembly-scan FindForwards path (already RUC-propagated). The IForwardsTo<> interface closure inspection preserves the user-registered forwarder type.")]
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
        // Route the scan through JasperFx's central TypeQuery (GH-2909) instead of an ad-hoc
        // Assembly.ExportedTypes walk. Concretes|Closed reproduces the previous
        // IsConcrete() && !IsOpenGeneric() candidate filter; the IForwardsTo<> closure check is unchanged.
        var query = new TypeQuery(TypeClassification.Concretes | TypeClassification.Closed);
        query.Includes.WithCondition("Closes IForwardsTo<>", t => t.Closes(typeof(IForwardsTo<>)));

        foreach (var type in query.Find([assembly])) Add(type);
    }
}