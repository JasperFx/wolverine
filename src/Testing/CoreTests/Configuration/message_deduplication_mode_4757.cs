using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
/// GH-4757. <c>DurabilitySettings.EnableMessageDeduplication</c> became a pass-through over the
/// three-value <see cref="MessageDeduplicationMode" />. These pin the mapping, because every existing
/// application's configuration code goes through the obsolete boolean and a wrong mapping would either
/// silently turn the feature off or silently reshape a deployed table.
/// </summary>
public class message_deduplication_mode_4757
{
    private readonly DurabilitySettings theSettings = new();

    [Fact]
    public void the_default_is_none()
    {
        theSettings.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.None);
    }

#pragma warning disable CS0618
    [Fact]
    public void the_obsolete_flag_defaults_to_false()
    {
        theSettings.EnableMessageDeduplication.ShouldBeFalse();
    }

    [Fact]
    public void setting_the_obsolete_flag_true_selects_hash_comparison()
    {
        theSettings.EnableMessageDeduplication = true;

        theSettings.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.CompareByHash);
    }

    [Fact]
    public void setting_the_obsolete_flag_false_selects_none()
    {
        theSettings.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;

        theSettings.EnableMessageDeduplication = false;

        theSettings.MessageDeduplicationMode.ShouldBe(MessageDeduplicationMode.None);
    }

    [Theory]
    [InlineData(MessageDeduplicationMode.None, false)]
    [InlineData(MessageDeduplicationMode.CompareByString, true)]
    [InlineData(MessageDeduplicationMode.CompareByHash, true)]
    public void the_obsolete_getter_reports_whether_any_mode_is_selected(MessageDeduplicationMode mode,
        bool expected)
    {
        theSettings.MessageDeduplicationMode = mode;

        theSettings.EnableMessageDeduplication.ShouldBe(expected);
    }
#pragma warning restore CS0618
}
