using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Recurring;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// The registration surface is the feature's OPT-IN, so what these pin is the boundary: a host
/// with zero schedules registers nothing and flips nothing, and the first registration wires
/// everything exactly once.
/// </summary>
public class RecurringMessageCollectionTests
{
    [Fact]
    public void a_host_with_no_schedules_opts_into_nothing()
    {
        var options = new WolverineOptions();

        options.Schedules.Any().ShouldBeFalse();
        options.Durability.EnableMessageDeduplication.ShouldBeFalse();
        options.Services.Any(x => x.ImplementationType == typeof(RecurringMessageAgent)).ShouldBeFalse();
        options.RegisteredPolicies.OfType<RecurringDeduplicationPolicy>().Any().ShouldBeFalse();
    }

    [Fact]
    public void registering_the_first_schedule_is_the_opt_in()
    {
        var options = new WolverineOptions();

        options.Schedules.ScheduleRecurring<SampleRecurringMessage>("0 9 * * *");

        options.Durability.EnableMessageDeduplication.ShouldBeTrue();
        options.Services.Count(x =>
                x.ServiceType == typeof(IAgentFamily) &&
                x.ImplementationType == typeof(RecurringMessageAgent))
            .ShouldBe(1);
        options.RegisteredPolicies.OfType<RecurringDeduplicationPolicy>().Count().ShouldBe(1);
    }

    [Fact]
    public void further_registrations_do_not_stack_agents_or_policies()
    {
        var options = new WolverineOptions();

        options.Schedules.ScheduleRecurring<SampleRecurringMessage>("0 9 * * *");
        options.Schedules.ScheduleRecurring("second", "0 2 * * *", _ => new OtherRecurringMessage());

        options.Schedules.Count.ShouldBe(2);
        options.Services.Count(x => x.ImplementationType == typeof(RecurringMessageAgent)).ShouldBe(1);
        options.RegisteredPolicies.OfType<RecurringDeduplicationPolicy>().Count().ShouldBe(1);
    }

    [Fact]
    public void the_no_argument_overload_names_the_schedule_after_the_message_type()
    {
        var options = new WolverineOptions();

        var message = options.Schedules.ScheduleRecurring<SampleRecurringMessage>("0 9 * * *");

        message.Name.ShouldBe(nameof(SampleRecurringMessage));
        message.MessageType.ShouldBe(typeof(SampleRecurringMessage));
    }

    [Fact]
    public void a_duplicate_name_is_refused_at_the_registration_site()
    {
        var options = new WolverineOptions();
        options.Schedules.ScheduleRecurring<SampleRecurringMessage>("0 9 * * *");

        // Names feed the occurrence dedup id — two schedules sharing one would dedupe against
        // each other and silently drop the second's occurrences.
        Should.Throw<ArgumentException>(() =>
                options.Schedules.ScheduleRecurring<SampleRecurringMessage>("0 10 * * *"))
            .Message.ShouldContain(nameof(SampleRecurringMessage));
    }

    [Fact]
    public void a_bad_cron_expression_fails_at_the_registration_site()
    {
        var options = new WolverineOptions();

        Should.Throw<ArgumentException>(() =>
            options.Schedules.ScheduleRecurring<SampleRecurringMessage>("every day at nine"));

        // Nothing half-registered: the failed call opted into nothing.
        options.Schedules.Any().ShouldBeFalse();
        options.Durability.EnableMessageDeduplication.ShouldBeFalse();
    }

    [Fact]
    public void a_null_creator_is_refused()
    {
        var options = new WolverineOptions();

        Should.Throw<ArgumentNullException>(() =>
            options.Schedules.ScheduleRecurring<SampleRecurringMessage>("named", "0 9 * * *", null!));
    }

    [Fact]
    public void the_factory_overload_hands_the_creator_the_occurrence_time()
    {
        var options = new WolverineOptions();
        var message = options.Schedules.ScheduleRecurring(
            "windowed", "0 2 * * *", occurrence => new WindowedMessage(occurrence.AddDays(-1), occurrence));

        var at = new DateTimeOffset(2026, 1, 15, 2, 0, 0, TimeSpan.Zero);
        var body = message.Creator(at).ShouldBeOfType<WindowedMessage>();

        body.From.ShouldBe(at.AddDays(-1));
        body.To.ShouldBe(at);
    }

    [Fact]
    public void schedules_are_findable_by_name()
    {
        var options = new WolverineOptions();
        options.Schedules.ScheduleRecurring<SampleRecurringMessage>("0 9 * * *");

        options.Schedules.FindByName(nameof(SampleRecurringMessage)).ShouldNotBeNull();
        options.Schedules.FindByName("nope").ShouldBeNull();
    }
}

public class OtherRecurringMessage;

public record WindowedMessage(DateTimeOffset From, DateTimeOffset To);
