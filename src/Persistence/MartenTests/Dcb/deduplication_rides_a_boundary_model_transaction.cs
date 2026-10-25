using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Tags;
using JasperFx.Resources;
using Marten;
using MartenTests.Dcb.University;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace MartenTests.Dcb;

public record ChangeCapacityOnce(CourseId CourseId, int Capacity);

/// <summary>GH-5205. Nothing in this signature names Marten: [BoundaryModel] applies the Marten transaction itself.</summary>
public static class ChangeCapacityOnceHandler
{
    public static EventTagQuery Load(ChangeCapacityOnce command)
        => EventTagQuery.For(command.CourseId).AndEventsOfType<CourseCreated, CourseCapacityChanged>();

    [Deduplicated]
    public static CourseCapacityChanged Handle(ChangeCapacityOnce command, [BoundaryModel] SubscriptionState state)
        => new(FacultyId.Default, command.CourseId, command.Capacity);
}

/// <summary>
/// GH-5205. A [Deduplicated] claim rides the Marten transaction on a chain whose only tie to Marten is a
/// [BoundaryModel], as it does for [WriteAggregate].
/// </summary>
public class deduplication_rides_a_boundary_model_transaction : IAsyncLifetime
{
    private const string SchemaName = "dcb_tx_dedup";

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;

                        m.Events.RegisterTagType<StudentId>("student").ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<CourseId>("course").ForAggregate<SubscriptionState>();
                        m.Events.RegisterTagType<FacultyId>("faculty");

                        m.Projections.LiveStreamAggregation<SubscriptionState>();

                        m.Events.AddEventType<CourseCreated>();
                        m.Events.AddEventType<CourseCapacityChanged>();

                        m.Events.StreamIdentity = StreamIdentity.AsString;
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ChangeCapacityOnceHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void the_claim_is_queued_onto_the_session_and_nothing_is_released()
    {
        theHost.GetRuntime().Handlers.HandlerFor<ChangeCapacityOnce>().ShouldNotBeNull();
        var chain = theHost.GetRuntime().Handlers.ChainFor<ChangeCapacityOnce>().ShouldNotBeNull();
        var code = chain.SourceCode.ShouldNotBeNull();

        code.ShouldContain($"{nameof(IMartenDeduplicator.QueueClaim)}(documentSession");
        code.ShouldNotContain("ReleaseAsync");
    }

    [Fact]
    public async Task a_replay_of_the_same_id_is_discarded()
    {
        var courseId = CourseId.Random();
        var store = theHost.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            var created = session.Events.BuildEvent(new CourseCreated(FacultyId.Default, courseId, "Math 101", 10));
            created.WithTag(courseId);
            session.Events.Append(courseId.Value, created);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var options = new DeliveryOptions { DeduplicationId = "capacity-" + courseId.Value };
        await theHost.SendMessageAndWaitAsync(new ChangeCapacityOnce(courseId, 20), options);
        await theHost.SendMessageAndWaitAsync(new ChangeCapacityOnce(courseId, 30), options);

        await using var query = store.LightweightSession();
        var events = await query.Events.QueryByTagsAsync(new EventTagQuery().Or<CourseId>(courseId),
            TestContext.Current.CancellationToken);
        events.Select(x => x.Data).OfType<CourseCapacityChanged>().ShouldHaveSingleItem().Capacity.ShouldBe(20);
    }
}
