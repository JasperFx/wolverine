#if NET11_0_OR_GREATER
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

/// <summary>
/// GH-5148. A .NET 11 <see cref="IAsyncStartupValidator"/> over the declared
/// <see cref="DurabilitySettings.ConnectionBudgets"/>. It runs once, inside <c>Host.StartAsync</c> and
/// before any hosted service, and only does anything when at least one budget has been declared with
/// <see cref="ConnectionBudgets.ForServer(string,int,int)"/>:
/// <list type="number">
/// <item>
/// A declared budget that names a server no statically known message store lives on refuses startup
/// with an <see cref="OptionsValidationException"/>. That budget is a typo or a stale entry, and it would
/// otherwise sit silently unused. Databases that are only discovered at runtime (multi-tenanted message
/// stores, dynamic tenancy) cannot be seen this early, so when the application has any multi-tenanted
/// message store an unmatched budget is logged as a warning instead.
/// </item>
/// <item>
/// Each known server is probed once and a warning is logged when its open connections already meet or
/// exceed the declared budget. This deliberately does not refuse startup: during a rolling or blue/green
/// deploy the other nodes legitimately hold most of the budget. A failing probe is logged, never fatal.
/// </item>
/// </list>
/// </summary>
internal class ConnectionBudgetStartupValidator : IAsyncStartupValidator
{
    private readonly WolverineOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<ConnectionBudgetStartupValidator> _logger;

    public ConnectionBudgetStartupValidator(WolverineOptions options, IServiceProvider services,
        ILogger<ConnectionBudgetStartupValidator> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        // Cheap gate first, before the runtime is even resolved: an application that declares no
        // budget pays nothing here and its startup order is untouched. (A budget declared only from an
        // async extension is not seen this early.)
        var budgets = _options.Durability.ConnectionBudgets;
        if (!budgets.HasAny || budgets.Enabled == false) return;

        if (_services.GetService(typeof(IWolverineRuntime)) is not WolverineRuntime runtime) return;

        if (!await runtime.PrepareForStartupValidationAsync().ConfigureAwait(false)) return;

        var stores = runtime.Stores;
        var probes = stores.AllStores()
            .OfType<IConnectionBudgetProbe>()
            .GroupBy(x => x.ServerId)
            .ToArray();

        var unmatched = budgets.UnmatchedServers(probes.Select(x => x.Key));
        if (unmatched.Count > 0)
        {
            var known = probes.Length == 0 ? "none" : probes.Select(x => x.Key.ToString()).Join(", ");

            if (stores.MultiTenanted.Any())
            {
                _logger.LogWarning(
                    "Durability.ConnectionBudgets declares a budget for {Servers}, which no message store known at startup lives on (known servers: {Known}). This application has multi-tenanted message stores whose databases are only discovered once Wolverine starts, so the budget may still apply to one of them",
                    unmatched.Join(", "), known);
            }
            else
            {
                var failures = unmatched.Select(server =>
                    $"Durability.ConnectionBudgets declares a budget for database server '{server}', but no message store in this application lives on that server (known servers: {known}). Check the server name and port against the connection string, or remove the stale budget. Databases discovered only at runtime by dynamic tenancy are not covered by this check.");

                throw new OptionsValidationException(nameof(DurabilitySettings.ConnectionBudgets),
                    typeof(ConnectionBudgets), failures);
            }
        }

        foreach (var group in probes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var max = budgets.MaxFor(group.Key);
            if (!max.HasValue) continue;

            try
            {
                var used = await group.First().CountServerConnectionsAsync(cancellationToken).ConfigureAwait(false);
                if (used >= max.Value)
                {
                    _logger.LogWarning(
                        "Database server {Server} already has {Used} open connections at startup against a declared connection budget of {Budget}. Startup continues; during a rolling deploy the other nodes may legitimately hold the budget",
                        group.Key, used, max.Value);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e,
                    "Unable to count the open connections on database server {Server} while validating its connection budget at startup",
                    group.Key);
            }
        }
    }
}
#endif
