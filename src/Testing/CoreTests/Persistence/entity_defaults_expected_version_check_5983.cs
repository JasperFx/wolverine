using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Persistence;

public class entity_defaults_expected_version_check_5983
{
    [Fact]
    public void the_expected_version_check_after_a_missing_aggregate_is_opt_in_on_6x()
    {
        // GH-5983. On 6.x an HTTP endpoint keeps passing the expected version to FetchForWriting unless the
        // application opts in, so a missing stream stays a concurrency failure by default
        new WolverineOptions().EntityDefaults.CheckExpectedVersionAfterMissingAggregate.ShouldBeFalse();
    }
}
