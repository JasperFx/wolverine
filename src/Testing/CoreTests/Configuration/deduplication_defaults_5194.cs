using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-5194. Logical deduplication storage (<see cref="DurabilitySettings.MessageDeduplicationMode" />) and
/// deduplicated responses (<see cref="DurabilitySettings.EnableDeduplicatedResponses" />) are on by default in
/// 7.0. <see cref="WolverineOptions.RestoreV6Defaults" /> turns both back off, and an explicit setting wins over
/// either default whichever order it is made in.
/// </summary>
public class deduplication_defaults_5194
{
    [Fact]
    public void the_7_0_defaults_compare_by_hash_and_store_deduplicated_responses()
    {
        var options = new WolverineOptions();

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByHash);
        options.Durability.EnableDeduplicatedResponses.ShouldBeTrue();
    }

    [Fact]
    public void RestoreV6Defaults_turns_both_off()
    {
        var options = new WolverineOptions();

        options.RestoreV6Defaults();

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.None);
        options.Durability.EnableDeduplicatedResponses.ShouldBeFalse();
    }

    [Fact]
    public void an_explicit_setting_made_before_RestoreV6Defaults_wins()
    {
        var options = new WolverineOptions();
        options.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByString;
        options.Durability.EnableDeduplicatedResponses = true;

        options.RestoreV6Defaults();

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByString);
        options.Durability.EnableDeduplicatedResponses.ShouldBeTrue();
    }

    [Fact]
    public void an_explicit_setting_made_after_RestoreV6Defaults_wins()
    {
        var options = new WolverineOptions();
        options.RestoreV6Defaults();

        options.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
        options.Durability.EnableDeduplicatedResponses = true;

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByHash);
        options.Durability.EnableDeduplicatedResponses.ShouldBeTrue();
    }

    [Fact]
    public void the_features_can_be_turned_off_without_restoring_every_6_x_default()
    {
        var options = new WolverineOptions();

        options.Durability.MessageDeduplicationMode = MessageDeduplicationMode.None;
        options.Durability.EnableDeduplicatedResponses = false;

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.None);
        options.Durability.EnableDeduplicatedResponses.ShouldBeFalse();
    }

    [Fact]
    public void a_recurring_message_keeps_deduplication_on_through_a_later_RestoreV6Defaults()
    {
        var options = new WolverineOptions();
        options.Schedules.ScheduleRecurring<DeduplicationDefaultsTick>("*/5 * * * *");

        options.RestoreV6Defaults();

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByHash);
    }

    [Fact]
    public void a_recurring_message_turns_deduplication_back_on_after_RestoreV6Defaults()
    {
        var options = new WolverineOptions();
        options.RestoreV6Defaults();

        options.Schedules.ScheduleRecurring<DeduplicationDefaultsTick>("*/5 * * * *");

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByHash);
    }

    [Fact]
    public void a_recurring_message_leaves_an_explicit_string_comparison_alone()
    {
        var options = new WolverineOptions();
        options.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByString;

        options.Schedules.ScheduleRecurring<DeduplicationDefaultsTick>("*/5 * * * *");
        options.RestoreV6Defaults();

        options.Durability.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByString);
    }
}

public record DeduplicationDefaultsTick;
