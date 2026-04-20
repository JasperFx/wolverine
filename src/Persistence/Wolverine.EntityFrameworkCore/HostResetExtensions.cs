using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore;

public static class HostResetExtensions
{
    /// <summary>
    ///     Reset the data for a single <see cref="DbContext"/> — delete every row in
    ///     foreign-key-safe order and then re-run every registered
    ///     <see cref="IInitialData{TContext}"/> seeder. A finer-grained alternative to
    ///     <c>host.ResetResourceState()</c> for integration tests that only need one
    ///     context cleaned between runs.
    /// </summary>
    /// <remarks>
    ///     Requires <c>WolverineOptions.UseEntityFrameworkCoreTransactions()</c> to have
    ///     registered the open-generic <see cref="DatabaseCleaner{TContext}"/>, which is
    ///     the default as of Wolverine 5.x (GH-2539). Creates its own scope so it can be
    ///     called from test fixtures that aren't already inside one.
    /// </remarks>
    /// <typeparam name="T">The DbContext type to reset.</typeparam>
    /// <param name="host">The running Wolverine/ASP.NET host.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public static async Task ResetAllDataAsync<T>(this IHost host, CancellationToken ct = default)
        where T : DbContext
    {
        using var scope = host.Services.CreateScope();

        // Resolving the DbContext first exercises any scoped factory/registration (e.g.
        // Wolverine's multi-tenant DbContext providers) before we ask the cleaner to act.
        _ = scope.ServiceProvider.GetRequiredService<T>();

        var cleaner = scope.ServiceProvider.GetRequiredService<DatabaseCleaner<T>>();
        await cleaner.ResetAllDataAsync(ct);
    }
}
