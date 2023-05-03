using System;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;

namespace Wolverine.ErrorHandling.Matches;

internal class ExcludeType<T> : IExceptionMatch where T : Exception
{
    public string Description => "Exclude " + typeof(T).FullNameInCode();

    public bool Matches(Exception ex)
    {
        return !(ex is T);
    }
}