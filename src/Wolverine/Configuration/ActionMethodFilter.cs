using System.Reflection;
using JasperFx.Core;

namespace Wolverine.Configuration;

internal class ActionMethodFilter : CompositeFilter<MethodInfo>
{
    public ActionMethodFilter()
    {
        Excludes += method => method.DeclaringType == typeof(object);
        Excludes += method => method.Name == nameof(IDisposable.Dispose);
        Excludes += method => method.ContainsGenericParameters;
        Excludes += method => method.IsSpecialName;
    }

    public void IgnoreMethodsDeclaredBy<T>()
    {
        Excludes += x => x.DeclaringType == typeof(T);
    }
}