using IntegrationTests;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine.Marten;

namespace Wolverine.Bobcat.Tests;

public sealed class AppointmentsHost : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "bobcat_specs";
                        m.DisableNpgsqlLogging = true;
                        m.Projections.Snapshot<Appointment>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ScheduleAppointmentHandler))
                    .IncludeType(typeof(ConfirmAppointmentHandler))
                    .IncludeType(typeof(SendConfirmationEmailHandler))
                    .IncludeType(typeof(PatientArrivedHandler));
            })
            .StartAsync();

        await Host.CleanAllMartenDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

[CollectionDefinition(nameof(AppointmentsCollection))]
public class AppointmentsCollection : ICollectionFixture<AppointmentsHost>;
