using Shouldly;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4246. The <c>description</c> column on <c>wolverine_node_records</c> is bounded on every store
/// that declares a width, and an <c>AssignmentChanged</c> description is an agent command's ToString()
/// -- an agent URI, a schema name and a destination node, which on a real cluster runs long. When it
/// overflowed the column the insert failed, and it took the whole AgentCommand batch behind it down
/// with it. These rows are append-only diagnostics: the tail is expendable, the insert is not.
/// </summary>
public class node_record_description_truncation
{
    [Fact]
    public void short_description_passes_through_untouched()
    {
        NodeRecord.TruncateDescription("wolverinedb://mysql/localhost/servix_local/servix_local")
            .ShouldBe("wolverinedb://mysql/localhost/servix_local/servix_local");
    }

    [Fact]
    public void null_and_empty_descriptions_become_empty_string()
    {
        NodeRecord.TruncateDescription(null).ShouldBe(string.Empty);
        NodeRecord.TruncateDescription(string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void a_description_exactly_at_the_limit_is_not_truncated()
    {
        var description = new string('x', NodeRecord.DescriptionLength);
        NodeRecord.TruncateDescription(description).ShouldBe(description);
    }

    [Fact]
    public void an_over_long_description_is_clamped_and_marked()
    {
        var truncated = NodeRecord.TruncateDescription(new string('x', NodeRecord.DescriptionLength * 3));

        truncated.Length.ShouldBe(NodeRecord.DescriptionLength);
        truncated.ShouldEndWith("...");
    }

    [Fact]
    public void the_declared_length_still_clears_the_description_that_broke_gh_4246()
    {
        // The exact record from the report -- an AssignAgent command against a non-default schema name.
        // It is ~230 characters, which cleared varchar(500) on SQL Server and Oracle and did not clear
        // MySQL's defaulted varchar(255).
        const string reported =
            "AssignAgent { AgentUri = wolverinedb://mysql/localhost/servix_local/servix_local, " +
            "Destination = NodeDestination { NodeId = a8965d0c-d6f5-45b9-8b52-fb1f50dde7ba, " +
            "ControlUri = dbcontrol://a8965d0c-d6f5-45b9-8b52-fb1f50dde7ba/ }, " +
            "DestinationNodeId = a8965d0c-d6f5-45b9-8b52-fb1f50dde7ba }";

        reported.Length.ShouldBeGreaterThan(255);
        NodeRecord.TruncateDescription(reported).ShouldBe(reported);
    }
}
