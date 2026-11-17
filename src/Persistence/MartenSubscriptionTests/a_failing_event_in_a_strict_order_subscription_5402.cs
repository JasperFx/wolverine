using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Xunit;

namespace MartenSubscriptionTests;

// GH-5402 step 1. Pins what ProcessEventsWithWolverineHandlersInStrictOrder() does TODAY when the handler fails
// for one event: with SkipApplyErrors (Marten's default for the running daemon) the event goes to Marten's dead
// letter storage and the subscription moves on to the next event, including the later events of the SAME
// stream. Nothing parks the rest of that stream. This describes current behavior, it is not a wish list.
public class a_failing_event_in_a_strict_order_subscription_5402
{
    private const string SchemaName = "subscription_dlq_5402";

    [Fact]
    public async Task later_events_in_the_same_stream_are_handled_after_the_failing_one_is_dead_lettered()
    {
        OrderedStepHandler.Handled.Clear();

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.DropSchemaAsync(SchemaName, TestContext.Current.CancellationToken);
        }

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(OrderedStepHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;

                        // Marten's default for the continuously running daemon, set here to be explicit
                        m.Projections.Errors.SkipApplyErrors = true;
                    })
                    .IntegrateWithWolverine()
                    .UseLightweightSessions()
                    .ProcessEventsWithWolverineHandlersInStrictOrder("OrderedSteps", s => s.IncludeType<OrderedStep>());
            }).StartAsync(TestContext.Current.CancellationToken);

        var store = host.Services.GetRequiredService<IDocumentStore>();
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        var failingStream = Guid.NewGuid();
        await using (var session = store.LightweightSession())
        {
            // "A" fails on its first event, "B" never fails
            session.Events.StartStream(failingStream,
                new OrderedStep("A", 1, Fails: true), new OrderedStep("A", 2), new OrderedStep("A", 3));
            session.Events.StartStream(Guid.NewGuid(), new OrderedStep("B", 1), new OrderedStep("B", 2));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await daemon.WaitForNonStaleData(30.Seconds());

        // A1 is in Marten's dead letter storage...
        await using var query = store.QuerySession();
        var deadLetters = await query.Query<DeadLetterEvent>().ToListAsync(TestContext.Current.CancellationToken);
        var deadLetter = deadLetters.ShouldHaveSingleItem();
        deadLetter.ExceptionType.ShouldContain(nameof(OrderedStepFailed));

        var failedEvent = await query.Events.FetchStreamAsync(failingStream, token: TestContext.Current.CancellationToken);
        deadLetter.EventSequence.ShouldBe(failedEvent[0].Sequence);

        // ...and the rest of stream A was handled anyway, in order, right after it
        OrderedStepHandler.Handled.ShouldBe(["A1", "A2", "A3", "B1", "B2"]);
    }
}

public record OrderedStep(string Stream, int Number, bool Fails = false);

public class OrderedStepFailed(string message) : Exception(message);

public static class OrderedStepHandler
{
    public static List<string> Handled { get; } = new();

    public static void Handle(OrderedStep step)
    {
        Handled.Add(step.Stream + step.Number);

        if (step.Fails)
        {
            throw new OrderedStepFailed($"{step.Stream}{step.Number} failed");
        }
    }
}
