using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using Marten;
using MartenTests.AggregateHandlerWorkflow;
using MartenTests.TestHelpers;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;

namespace MartenTests.Bugs;

public class report_critical_error_pauses_subscription
{
    //[Fact] -- keeping this around, but can't run this w/ the 2 minute wait
    public async Task try_it_out()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "debug";

                        m.Projections.Errors.SkipApplyErrors = false; // default is true
                        m.Projections.RebuildErrors.SkipApplyErrors =
                            false; // default is false, but keep it to be explicit
                        m.Projections.Errors.SkipSerializationErrors = false; // default is true
                        m.Projections.RebuildErrors.SkipSerializationErrors =
                            false; // default is false, but keep it to be explicit
                        m.Projections.Errors.SkipUnknownEvents = true; // default is true, but keep it to be explicit
                        m.Projections.RebuildErrors.SkipUnknownEvents =
                            false; // default is false, but keep it to be explicit

                    }).IntegrateWithWolverine()
                    .ProcessEventsWithWolverineHandlersInStrictOrder("S3Handler", o =>
                    {
                        o.IncludeType<S3Event>();
                        o.IncludeType<AEvent>();
                        o.IncludeType<BEvent>();
                        o.IncludeType<CEvent>();
                        o.IncludeType<DEvent>();
                        o.IncludeType<EEvent>();
                        o.Options.BatchSize = 10; // how many events to process at a time
                        //o.Options.SubscribeFromPresent();
                    })
                    .UseLightweightSessions();
            }).StartAsync();
        
        var store = host.DocumentStore();
        await store.Advanced.Clean.DeleteAllEventDataAsync();
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();
        
        using var session = store.LightweightSession();

        for (int i = 0; i < 10; i++)
        {
            session.Events.StartStream(LetterEvents.ToRandomEvents());
            session.Events.StartStream(LetterEvents.ToRandomEvents());
            session.Events.StartStream(LetterEvents.ToRandomEvents());
            session.Events.StartStream(LetterEvents.ToRandomEvents());
            await session.SaveChangesAsync();
        }

        session.Events.StartStream(new S3Event());
        await session.SaveChangesAsync();

        await Task.Delay(2.Minutes());
    }
}

public record S3Event;

public class S3Handler
{
    public static void Handle(AEvent e) => Debug.WriteLine("Got AEvent");
    public static void Handle(BEvent e) => Debug.WriteLine("Got BEvent");
    public static void Handle(CEvent e) => Debug.WriteLine("Got CEvent");
    public static void Handle(DEvent e) => Debug.WriteLine("Got DEvent");
    
    public Task HandleAsync(S3Event @event, IDocumentSession session)
    {
        throw new Exception("Not Implemented");
    }
}