using JasperFx;
using Marten;
using Marten.Events.Projections;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using TeleHealth.Common;

var builder = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddMarten(opts =>
            {
                opts.Connection(ConnectionSource.ConnectionString);

                opts.Projections.Add<AppointmentProjection>(ProjectionLifecycle.Inline);
                opts.Projections.Snapshot<ProviderShift>(SnapshotLifecycle.Inline);

                opts.Projections.Add<BoardViewProjection>(ProjectionLifecycle.Async);
            })
            .AddAsyncDaemon(DaemonMode.HotCold);
    });

return await builder.RunJasperFxCommands(args);