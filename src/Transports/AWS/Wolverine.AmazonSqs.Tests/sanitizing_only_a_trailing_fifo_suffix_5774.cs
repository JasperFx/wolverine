using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests;

// GH-5774: only a TRAILING ".fifo" is the FIFO suffix. A ".fifo" anywhere else in the name is just
// part of the name and gets sanitized like the rest of it.
public class sanitizing_only_a_trailing_fifo_suffix_5774
{
    [Theory]
    [InlineData("My.App.FifoOrderPlaced", "My-App-FifoOrderPlaced")]
    [InlineData("orders.fifo.v2", "orders-fifo-v2")]
    [InlineData("orders.fifo.v2.fifo", "orders-fifo-v2.fifo")]
    [InlineData("orders.FIFO", "orders.FIFO")]
    [InlineData("orders.Fifo", "orders.Fifo")]
    [InlineData("orders.fifo", "orders.fifo")]
    [InlineData(".fifo", ".fifo")]
    public void only_a_trailing_fifo_is_the_suffix(string identifier, string expected)
    {
        AmazonSqsTransport.SanitizeSqsName(identifier).ShouldBe(expected);
    }

    [Fact]
    public void a_type_name_starting_with_fifo_is_a_legal_queue_name()
    {
        var name = AmazonSqsTransport.SanitizeSqsName("Wolverine.AmazonSqs.Tests.FifoRequeuePing");

        name.ShouldBe("Wolverine-AmazonSqs-Tests-FifoRequeuePing");
        name.ShouldNotContain(".");
    }
}
