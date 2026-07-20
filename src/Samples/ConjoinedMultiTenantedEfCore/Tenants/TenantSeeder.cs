using JasperFx.MultiTenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConjoinedMultiTenantedEfCore.Tenants;

// Purely for demo convenience: register our two fictional tenants in the
// wolverine_tenants registry at startup so the sample is immediately usable.
// AddTenantAsync is an upsert, so restarting the application is harmless.
//
// This hosted service is registered *after* AddResourceSetupOnStartup() in
// Program.cs, so the registry table is guaranteed to exist by the time it runs
public class TenantSeeder : IHostedService
{
    private readonly IDynamicTenantSource<string> _tenants;
    private readonly ILogger<TenantSeeder> _logger;

    public TenantSeeder(IDynamicTenantSource<string> tenants, ILogger<TenantSeeder> logger)
    {
        _tenants = tenants;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _tenants.AddTenantAsync("acme", cancellationToken);
        await _tenants.AddTenantAsync("initech", cancellationToken);
        _logger.LogInformation("Seeded conjoined tenants 'acme' and 'initech'");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
