using JasperFx.Core.Reflection;

namespace Wolverine.ErrorHandling.Matches;

internal class TypeMatch<T> : IExceptionMatch where T : Exception
{
    public string Description => "Exception is " + typeof(T).FullNameInCode();

    public bool Matches(Exception ex)
    {
        return ex is T;
    }
}