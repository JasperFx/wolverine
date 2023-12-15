using IntegrationTests;
using JasperFx.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using PersistenceTests.EFCore;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime.Interop.MassTransit;
using Xunit;
using Xunit.Sdk;

namespace PersistenceTests.Postgresql;

public class Bug_661_postgresql_with_ef_core 
{
#if NET8_0_OR_GREATER
    [Fact]
    public async Task can_set_up_with_default_schema_name()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString!);
                opts.Services.AddResourceSetupOnStartup();
                opts.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(Servers.PostgresConnectionString));
            }).StartAsync();


        
    }
#endif
}