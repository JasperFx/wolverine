using Xunit;

namespace CoreTests.Analyzers;

public class WOLV0003_cascading_tuple_contains_a_task
{
    private const string Usings = """
        using System;
        using System.Threading.Tasks;
        using Wolverine;
        using Wolverine.Attributes;

        public record PlaceOrder(string Id);
        public record OrderPlaced(string Id);
        public record AuditOrder(string Id);

        """;

    private static async Task<string[]> diagnosticsFor(string source)
    {
        var diagnostics = await AnalyzerHarness.DiagnosticsFor(Usings + source);
        return diagnostics.Select(x => x.Describe()).ToArray();
    }

    [Fact]
    public async Task a_task_in_the_tuple()
    {
        (await diagnosticsFor("""
            public static class PlaceOrderHandler
            {
                public static (OrderPlaced, Task) Handle(PlaceOrder command)
                    => (new OrderPlaced(command.Id), Task.CompletedTask);
            }
            """)).ShouldBe(["WOLV0003: (OrderPlaced, Task)"]);
    }

    [Fact]
    public async Task a_value_task_inside_an_async_tuple()
    {
        (await diagnosticsFor("""
            public class PlaceOrderHandler
            {
                public async Task<(OrderPlaced, ValueTask<AuditOrder>)> HandleAsync(PlaceOrder command)
                {
                    await Task.Yield();
                    return (new OrderPlaced(command.Id), ValueTask.FromResult(new AuditOrder(command.Id)));
                }
            }
            """)).ShouldBe(["WOLV0003: Task<(OrderPlaced, ValueTask<AuditOrder>)>"]);
    }

    [Fact]
    public async Task a_method_marked_WolverineHandler()
    {
        (await diagnosticsFor("""
            public class OrderHandler
            {
                [WolverineHandler]
                public (OrderPlaced, Task<AuditOrder>) Place(PlaceOrder command)
                    => (new OrderPlaced(command.Id), Task.FromResult(new AuditOrder(command.Id)));
            }
            """)).ShouldBe(["WOLV0003: (OrderPlaced, Task<AuditOrder>)"]);
    }

    [Fact]
    public async Task an_http_endpoint()
    {
        // Stands in for Wolverine.Http, which CoreTests doesn't reference
        (await diagnosticsFor("""
            namespace Wolverine.Http
            {
                public abstract class WolverineHttpMethodAttribute : Attribute;
                public class WolverinePostAttribute(string route) : WolverineHttpMethodAttribute;
            }

            public static class OrderEndpoint
            {
                [Wolverine.Http.WolverinePost("/orders")]
                public static (OrderPlaced, Task) Post(PlaceOrder command)
                    => (new OrderPlaced(command.Id), Task.CompletedTask);
            }
            """)).ShouldBe(["WOLV0003: (OrderPlaced, Task)"]);
    }

    [Fact]
    public async Task tuples_of_messages_are_fine()
    {
        (await diagnosticsFor("""
            public class PlaceOrderHandler
            {
                public (OrderPlaced, AuditOrder) Handle(PlaceOrder command)
                    => (new OrderPlaced(command.Id), new AuditOrder(command.Id));

                public async Task<(OrderPlaced, AuditOrder)> HandleAsync(AuditOrder command)
                {
                    await Task.Yield();
                    return (new OrderPlaced(command.Id), command);
                }
            }

            // Not a handler, so its tuples are its own business
            public class OrderMath
            {
                public (int, Task) Handle(PlaceOrder command) => (1, Task.CompletedTask);
            }
            """)).ShouldBeEmpty();
    }
}
