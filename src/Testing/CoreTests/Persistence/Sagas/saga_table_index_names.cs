using Shouldly;
using Wolverine.RDBMS.Sagas;
using Xunit;

namespace CoreTests.Persistence.Sagas;

public class saga_table_index_names
{
    [Fact]
    public void a_short_table_name_keeps_the_plain_index_name()
    {
        SagaTableIndexes.LastModifiedIndexName("order_saga", 63).ShouldBe("idx_order_saga_last_modified");
    }

    [Fact]
    public void a_long_table_name_is_shortened_within_the_limit_and_stays_unique_and_stable()
    {
        var first = SagaTableIndexes.LastModifiedIndexName(new string('a', 70) + "_one", 64);
        var second = SagaTableIndexes.LastModifiedIndexName(new string('a', 70) + "_two", 64);

        first.Length.ShouldBeLessThanOrEqualTo(64);
        second.Length.ShouldBeLessThanOrEqualTo(64);
        first.ShouldNotBe(second);
        SagaTableIndexes.LastModifiedIndexName(new string('a', 70) + "_one", 64).ShouldBe(first);
    }
}
