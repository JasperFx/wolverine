using JasperFx.Core;

namespace Wolverine.Util;

internal static class DisposalExtensions
{
    /// <summary>
    /// Disposes <paramref name="target" /> asynchronously when it is <see cref="IAsyncDisposable" />, otherwise
    /// synchronously and swallowing any exception when it is <see cref="IDisposable" />
    /// </summary>
    public static ValueTask MaybeDisposeAsync(this object? target)
    {
        switch (target)
        {
            case IAsyncDisposable asyncDisposable:
                return asyncDisposable.DisposeAsync();
            case IDisposable disposable:
                disposable.SafeDispose();
                break;
        }

        return ValueTask.CompletedTask;
    }
}
