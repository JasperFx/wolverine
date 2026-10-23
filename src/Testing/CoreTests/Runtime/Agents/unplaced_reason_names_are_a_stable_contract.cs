using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
///     GH-5163 follow-up. CritterWatch carries <see cref="UnplacedReason" /> by its member NAME on the wire and
///     persists it into alert records (JasperFx/CritterWatch#1460), so renaming or removing a member orphans
///     data that is already stored. Adding a member is fine: CritterWatch maps a name it does not know to
///     "unknown". If this test fails, put the old name back -- do not update the list.
/// </summary>
public class unplaced_reason_names_are_a_stable_contract
{
    // String literals on purpose: a rename refactoring updates nameof() along with the member it renames
    public static TheoryData<string> PersistedNames =>
    [
        "AwaitingCapacity",
        "NoCapableNode",
        "Paused",
        "TakeoverHold",
        "Superseded"
    ];

    [Theory]
    [MemberData(nameof(PersistedNames))]
    public void a_persisted_reason_name_still_parses(string name)
    {
        Enum.TryParse<UnplacedReason>(name, ignoreCase: false, out var reason).ShouldBeTrue(
            $"UnplacedReason.{name} is persisted by CritterWatch by name and must never be renamed or removed");

        reason.ToString().ShouldBe(name);
    }
}
