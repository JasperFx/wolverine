using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.Core.Reflection;
using JasperFx.TypeDiscovery;

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

        if (Relationships.ContainsKey(type))
        {
            Relationships[type] = forwardedType;
        }
        else
        {
            Relationships.Add(type, forwardedType);
        }
    }

    public void FindForwards(Assembly assembly)
    {
        var candidates = assembly.ExportedTypes.Where(x => x.IsConcrete() && !x.IsOpenGeneric());
        foreach (var type in candidates.Where(t => t.Closes(typeof(IForwardsTo<>)))) Add(type);
    }
}