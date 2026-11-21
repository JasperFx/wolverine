namespace Wolverine.Runtime;

/// <summary>
/// GH-5723. Disposing a host without stopping it first (<c>using var host = ...</c>, <c>IHost.Dispose()</c>) runs no
/// hosted service's <c>StopAsync</c>, so Wolverine only stopped when the container got to disposing the runtime itself.
/// The container disposes singletons in the reverse of the order it created them, and a message store's data source
/// is created during Wolverine's startup, after the runtime, so it was disposed first. Wolverine's shutdown then ran
/// against a disposed data source, and the durability agent logged <see cref="ObjectDisposedException" /> errors.
///
/// <para>The runtime resolves this as the last step of its startup, so it is the first of Wolverine's singletons to
/// be disposed, and it stops the runtime while everything the shutdown needs still exists. <c>StopAsync</c> is
/// single-entry: when the host was stopped first, or the runtime is disposed later, they share this same
/// shutdown.</para>
/// </summary>
internal sealed class StopWolverineBeforeItsServicesAreDisposed : IAsyncDisposable
{
    private readonly IWolverineRuntime _runtime;

    public StopWolverineBeforeItsServicesAreDisposed(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask DisposeAsync()
    {
        if (_runtime is WolverineRuntime runtime)
        {
            await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
