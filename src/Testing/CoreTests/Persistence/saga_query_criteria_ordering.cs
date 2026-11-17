using Shouldly;
using Wolverine.Persistence.Sagas;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
///     GH-5142. The saga query ordering is tie-broken by the saga identity only when the ordering does not
///     already name it. "order by id, id" is accepted by PostgreSQL and refused by SQL Server, so a default
///     ordered saga listing failed on Polecat.
/// </summary>
public class saga_query_criteria_ordering
{
    [Theory]
    [InlineData("Id", true)]
    [InlineData("id", true)]
    [InlineData("Id desc", true)]
    [InlineData("Note desc, Id", true)]
    [InlineData(" Note ,  Id asc ", true)]
    [InlineData("Note desc", false)]
    [InlineData("Identity", false)]
    [InlineData("NoteId", false)]
    public void knows_when_an_ordering_already_names_the_identity(string orderBy, bool expected)
    {
        SagaQueryCriteria.OrdersBy(orderBy, "Id").ShouldBe(expected);
    }
}
