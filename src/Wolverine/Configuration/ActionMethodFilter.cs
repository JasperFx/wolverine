using System;
using System.Reflection;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Util;

namespace Wolverine.Configuration;

internal class ActionMethodFilter : CompositeFilter<MethodInfo>
{
    public ActionMethodFilter()
    {
        Excludes += method => method.DeclaringType == typeof(object);
        Excludes += method => method.Name == nameof(IDisposable.Dispose);
        Excludes += method => method.ContainsGenericParameters;
        Excludes += method => method.IsSpecialName;
        Excludes += method => method.HasAttribute<WolverineIgnoreAttribute>();
    }

    public void IgnoreMethodsDeclaredBy<T>()
    {
        Excludes += x => x.DeclaringType == typeof(T);
    }
}

internal class HandlerTypeFilter : CompositeFilter<Type>
{
    public HandlerTypeFilter()
    {
        Excludes += t => !t.IsStatic() && t.IsOpenGeneric();
        Excludes += t => t.IsNotPublic;
        Excludes += t => !t.IsStatic() && t.IsNotConcrete();

        Includes += t => t.IsStatic();
        Includes += t => t.IsConcrete();
    }
}

