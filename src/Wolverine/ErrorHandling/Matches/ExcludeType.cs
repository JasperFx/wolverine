using JasperFx.CodeGeneration;

namespace Wolverine.ErrorHandling.Matches;

internal class ExcludeType<T> : IExceptionMatch where T : Exception
{
    public string Description => "Exclude " + typeof(T).FullNameInCode();

    public bool Matches(Exception ex)
    {
        return !(ex is T);
    }
}