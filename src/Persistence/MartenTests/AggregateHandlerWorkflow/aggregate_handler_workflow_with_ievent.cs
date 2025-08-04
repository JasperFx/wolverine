using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace MartenTests.AggregateHandlerWorkflow;

public class aggregate_handler_workflow_with_ievent
{
    private readonly ITestOutputHelper _output;

    public aggregate_handler_workflow_with_ievent(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task use_ievent_as_Guid_id()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AEventHandler)).IncludeType(typeof(RaiseLetterHandler));
                
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);

                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine(x =>
                    {
                        x.UseFastEventForwarding = true;
                    });
                
                opts.Policies.AutoApplyTransactions();
                

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.DocumentStore();
        using var session = store.LightweightSession();
        
        var streamId = Guid.NewGuid();

        session.Events.StartStream<LetterAggregate>(streamId, new AEvent(), new BEvent());
        await session.SaveChangesAsync();

        var tracked = await host.InvokeMessageAndWaitAsync(new RaiseABC(streamId));

        tracked.Executed.SingleEnvelope<IEvent<AEvent>>().ShouldNotBeNull();

        var doc = await session.LoadAsync<LetterAggregate>(streamId);
        doc.DCount.ShouldBe(1);
    }

    [Fact]
    public async Task using_string_as_stream_key()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(StringIdentifiedHandler));
                
                opts.Services.AddMarten(m =>
                    {
                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "as_string";
                        m.Projections.Add<LetterCountsByStringProjection>(ProjectionLifecycle.Inline);

                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine(x =>
                    {
                        x.UseFastEventForwarding = true;
                    });
                
                opts.Policies.AutoApplyTransactions();
                

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.DocumentStore();
        using var session = store.LightweightSession();
        
        var streamKey = Guid.NewGuid().ToString();

        var tracked = await host.InvokeMessageAndWaitAsync(new StartLetterCountsByString(streamKey));

        tracked.Executed.SingleEnvelope<IEvent<AEvent>>().ShouldNotBeNull();

        var doc = await session.LoadAsync<LetterCountsByString>(streamKey);
        doc.DCount.ShouldBe(1);
    }
    

}

public static class AEventHandler
{
    
    
    [AggregateHandler]
    public static DEvent Handle(IEvent<AEvent> _, LetterAggregate aggregate)
    {
        return new DEvent();
    }
}

public class LetterCountsByString: IRevisioned
{
    public string Id { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }
    public int Version { get; set; }
}

public record StartLetterCountsByString(string StreamKey);

public static class StringIdentifiedHandler
{
    public static IStartStream Handle(StartLetterCountsByString command)
    {
        return MartenOps.StartStream<LetterCountsByString>(command.StreamKey, new AEvent(), new BEvent(), new CEvent());
    }

    [AggregateHandler]
    public static DEvent Handle(IEvent<AEvent> e, LetterCountsByString aggregate)
    {
        aggregate.Id.ShouldBe(e.StreamKey);
        return new DEvent();
    }
}

public class LetterCountsByStringProjection: SingleStreamProjection<LetterCountsByString, string>
{
    public override LetterCountsByString Evolve(LetterCountsByString snapshot, string id, IEvent e)
    {
        snapshot ??= new LetterCountsByString { Id = id };

        switch (e.Data)
        {
            case AEvent _:
                snapshot.ACount++;
                break;

            case BEvent _:
                snapshot.BCount++;
                break;

            case CEvent _:
                snapshot.CCount++;
                break;

            case DEvent _:
                snapshot.DCount++;
                break;
        }

        return snapshot;
    }
}
