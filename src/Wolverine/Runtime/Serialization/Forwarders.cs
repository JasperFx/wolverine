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

    public async Task FindForwardsAsync(Assembly assembly)
    {
        var candidates = await TypeRepository.ForAssembly(assembly);
        foreach (var type in candidates.ClosedTypes.Concretes.Where(t => t.Closes(typeof(IForwardsTo<>)))) Add(type);
    }
}