using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace PostgresqlTests;

public class deduplication_outcome_telemetry : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "outcome-telemetry";
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dedup_outcomes");
                opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
                opts.Durability.DeduplicationWindow = 1.Hours();

                opts.OnException<OutcomeAlwaysFails>().Discard();
                opts.OnException<OutcomeFailsOnce>().RetryOnce();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(OutcomeCountedHandler))
                    .IncludeType(typeof(OutcomeFailingHandler))
                    .IncludeType(typeof(OutcomeRequiredIdHandler))
                    .IncludeType(typeof(OutcomeOptionalIdHandler))
                    .IncludeType(typeof(OutcomeShippingHandler))
                    .IncludeType(typeof(OutcomeBillingHandler));
            }).StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();
        OutcomeFailingHandler.Attempts = 0;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static DeliveryOptions keyed(string id) => new() { DeduplicationId = id };

    [Fact]
    public async Task the_first_message_counts_as_claimed_under_its_message_type_and_handler()
    {
        using var outcomes = new MessageDeduplicationOutcomeListener(theHost);

        await theHost.SendMessageAndWaitAsync(new OutcomeCounted("first"), keyed(Guid.NewGuid().ToString()));

        var outcome = outcomes.All.ShouldHaveSingleItem();
        outcome.Outcome.ShouldBe("claimed");
        outcome.MessageType.ShouldBe(typeof(OutcomeCounted).ToMessageTypeName());
        outcome.Handler.ShouldBe(typeof(OutcomeCountedHandler).FullNameInCode());
        outcome.Source.ShouldBe("outcome-telemetry");
    }

    [Fact]
    public async Task a_repeat_counts_as_a_duplicate_and_not_as_another_claim()
    {
        var id = Guid.NewGuid().ToString();
        await theHost.SendMessageAndWaitAsync(new OutcomeCounted("first"), keyed(id));

        using var outcomes = new MessageDeduplicationOutcomeListener(theHost);

        await theHost.SendMessageAndWaitAsync(new OutcomeCounted("repeat"), keyed(id));

        outcomes.For(typeof(OutcomeCountedHandler)).ShouldBe(["duplicate"]);
    }

    [Fact]
    public async Task a_message_without_an_id_counts_nothing()
    {
        using var outcomes = new MessageDeduplicationOutcomeListener(theHost);

        await theHost.SendMessageAndWaitAsync(new OutcomeOptionalId());

        outcomes.All.ShouldBeEmpty();

        await theHost.SendMessageAndWaitAsync(new OutcomeOptionalId(), keyed(Guid.NewGuid().ToString()));

        outcomes.For(typeof(OutcomeOptionalIdHandler)).ShouldBe(["claimed"]);
    }

    [Fact]
    public async Task a_missing_required_id_counts_nothing()
    {
        using var outcomes = new MessageDeduplicationOutcomeListener(theHost);

        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new OutcomeRequiredId());

        outcomes.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_handler_that_throws_counts_nothing_and_its_retry_claims()
    {
        using var outcomes = new MessageDeduplicationOutcomeListener(theHost);

        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new OutcomeFailing(FailsOnce: false), keyed(Guid.NewGuid().ToString()));

        outcomes.All.ShouldBeEmpty();

        await theHost.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new OutcomeFailing(FailsOnce: true), keyed(Guid.NewGuid().ToString()));

        OutcomeFailingHandler.Attempts.ShouldBe(2);
        outcomes.For(typeof(OutcomeFailingHandler)).ShouldBe(["claimed"]);
    }

    [Fact]
    public async Task each_handler_chain_of_a_message_type_counts_under_its_own_handler()
    {
        using var outcomes = new MessageDeduplicationOutcomeListener(theHost);
        var id = Guid.NewGuid().ToString();

        await theHost.TrackActivity().PublishMessageAndWaitAsync(new OutcomeShipped("first"), keyed(id));
        await theHost.TrackActivity().PublishMessageAndWaitAsync(new OutcomeShipped("repeat"), keyed(id));

        outcomes.For(typeof(OutcomeShippingHandler)).ShouldBe(["claimed", "duplicate"]);
        outcomes.For(typeof(OutcomeBillingHandler)).ShouldBe(["claimed", "duplicate"]);
        outcomes.All.ShouldAllBe(x => x.MessageType == typeof(OutcomeShipped).ToMessageTypeName());
    }

    [Fact]
    public async Task the_outcome_is_an_event_on_the_message_span()
    {
        using var spans = new MessageDeduplicationOutcomeListener.MessageSpans();
        var id = Guid.NewGuid().ToString();

        await theHost.SendMessageAndWaitAsync(new OutcomeCounted("first"), keyed(id));
        await theHost.SendMessageAndWaitAsync(new OutcomeCounted("repeat"), keyed(id));

        (await spans.DeduplicationEventsOf(typeof(OutcomeCounted).ToMessageTypeName(), 2))
            .ShouldBe([WolverineTracing.DeduplicationClaimed, WolverineTracing.DeduplicationDuplicate]);
    }
}

public record OutcomeCounted(string Name);

public record OutcomeFailing(bool FailsOnce);

public record OutcomeRequiredId;

public record OutcomeOptionalId;

public record OutcomeShipped(string Name);

public class OutcomeAlwaysFails : Exception;

public class OutcomeFailsOnce : Exception;

[WolverineIgnore]
public class OutcomeCountedHandler
{
    [Deduplicated]
    public void Handle(OutcomeCounted message)
    {
    }
}

[WolverineIgnore]
public class OutcomeFailingHandler
{
    public static int Attempts;

    [Deduplicated]
    public void Handle(OutcomeFailing message)
    {
        if (!message.FailsOnce) throw new OutcomeAlwaysFails();
        if (Interlocked.Increment(ref Attempts) == 1) throw new OutcomeFailsOnce();
    }
}

[WolverineIgnore]
public class OutcomeRequiredIdHandler
{
    [Deduplicated(Required = true)]
    public void Handle(OutcomeRequiredId message)
    {
    }
}

[WolverineIgnore]
public class OutcomeOptionalIdHandler
{
    [Deduplicated(Required = false)]
    public void Handle(OutcomeOptionalId message)
    {
    }
}

[WolverineIgnore]
public class OutcomeShippingHandler
{
    [Deduplicated]
    public void Handle(OutcomeShipped message)
    {
    }
}

[WolverineIgnore]
public class OutcomeBillingHandler
{
    [Deduplicated]
    public void Handle(OutcomeShipped message)
    {
    }
}
