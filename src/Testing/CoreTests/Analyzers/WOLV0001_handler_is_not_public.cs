using Xunit;

namespace CoreTests.Analyzers;

public class WOLV0001_handler_is_not_public
{
    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Wolverine;
        using Wolverine.Attributes;
        using Wolverine.Runtime;
        using Wolverine.Runtime.Handlers;

        public record PlaceOrder(string Id);

        """;

    private static async Task<string[]> diagnosticsFor(string source)
    {
        var diagnostics = await AnalyzerHarness.DiagnosticsFor(Usings + source);
        return diagnostics.Select(x => x.Describe()).ToArray();
    }

    [Fact]
    public async Task an_internal_handler_class()
    {
        (await diagnosticsFor("""
            internal class PlaceOrderHandler
            {
                public void Handle(PlaceOrder command) { }
            }
            """)).ShouldBe(["WOLV0001: PlaceOrderHandler"]);
    }

    [Fact]
    public async Task an_internal_consumer_class()
    {
        (await diagnosticsFor("""
            internal static class PlaceOrderConsumer
            {
                public static Task ConsumeAsync(PlaceOrder command) => Task.CompletedTask;
            }
            """)).ShouldBe(["WOLV0001: PlaceOrderConsumer"]);
    }

    [Fact]
    public async Task a_private_nested_handler_class()
    {
        (await diagnosticsFor("""
            public class Tests
            {
                private class PlaceOrderHandler
                {
                    public void Handle(PlaceOrder command) { }
                }
            }
            """)).ShouldBe(["WOLV0001: PlaceOrderHandler"]);
    }

    [Fact]
    public async Task an_internal_IWolverineHandler()
    {
        (await diagnosticsFor("""
            internal class Orders : IWolverineHandler
            {
                public void Handle(PlaceOrder command) { }
            }
            """)).ShouldBe(["WOLV0001: Orders"]);
    }

    [Fact]
    public async Task private_and_protected_handler_methods()
    {
        (await diagnosticsFor("""
            public class PlaceOrderHandler
            {
                private void Handle(PlaceOrder command) { }
                protected Task ConsumeAsync(PlaceOrder command) => Task.CompletedTask;
                internal void Handles(PlaceOrder command) { }
            }
            """)).ShouldBe([
            "WOLV0001: Handle",
            "WOLV0001: ConsumeAsync",
            "WOLV0001: Handles"
        ]);
    }

    [Fact]
    public async Task a_private_method_marked_WolverineHandler()
    {
        (await diagnosticsFor("""
            [WolverineHandler]
            public class Orders
            {
                [WolverineHandler]
                private void Place(PlaceOrder command) { }
            }
            """)).ShouldBe(["WOLV0001: Place"]);
    }

    [Fact]
    public async Task saga_method_names_on_a_saga()
    {
        (await diagnosticsFor("""
            public class OrderSaga : Saga
            {
                public string? Id { get; set; }

                private void Start(PlaceOrder command) { }
                private void NotFound(PlaceOrder command) { }
            }
            """)).ShouldBe(["WOLV0001: Start", "WOLV0001: NotFound"]);
    }

    [Fact]
    public async Task public_handlers_are_fine()
    {
        (await diagnosticsFor("""
            public class PlaceOrderHandler
            {
                public void Handle(PlaceOrder command) { }
            }

            public static class PlaceOrderConsumer
            {
                public static Task ConsumeAsync(PlaceOrder command, CancellationToken token) => Task.CompletedTask;
            }

            public class Outer
            {
                public class NestedHandler
                {
                    public void Handle(PlaceOrder command) { }
                }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public async Task saga_method_names_are_not_reserved_on_other_handler_types()
    {
        (await diagnosticsFor("""
            public class PlaceOrderHandler
            {
                public void Handle(PlaceOrder command) { }

                private void Start(PlaceOrder command) { }
                private void NotFound(PlaceOrder command) { }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public async Task types_wolverine_would_not_discover_anyway()
    {
        (await diagnosticsFor("""
            // Not named like a handler
            internal class Orders
            {
                public void Handle(PlaceOrder command) { }
            }

            // Abstract
            internal abstract class BaseHandler
            {
                public void Handle(PlaceOrder command) { }
            }

            // Open generic
            internal class GenericHandler<T>
            {
                public void Handle(PlaceOrder command) { }
            }

            // Explicitly ignored
            [WolverineIgnore]
            internal class IgnoredHandler
            {
                public void Handle(PlaceOrder command) { }
            }

            public class PartlyIgnoredHandler
            {
                public void Handle(PlaceOrder command) { }

                [WolverineIgnore]
                private void Handle(string reason, PlaceOrder command) { }
            }

            // A hand written MessageHandler is registered directly, not discovered
            internal class CustomHandler : MessageHandler
            {
                public override Task HandleAsync(MessageContext context, CancellationToken cancellation) => Task.CompletedTask;
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public async Task methods_that_only_share_a_name_with_a_handler()
    {
        (await diagnosticsFor("""
            public class PlaceOrderHandler
            {
                public void Handle(PlaceOrder command) { }

                // No message
                private void Handle() { }

                // Framework or primitive types, not messages
                private void Handle(Exception e) { }
                private void Consume(string text) { }
                private void Handle(CancellationToken token) { }

                // Returns a primitive
                private bool Handle(PlaceOrder command, int attempt) => true;

                // Generic method
                private void Handle<T>(T message) { }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public async Task another_frameworks_handler_contract_is_left_alone()
    {
        (await diagnosticsFor("""
            public interface IRequestHandler<T>
            {
                Task Handle(T request, CancellationToken token);
            }

            // A MediatR style handler in an application that also uses Wolverine
            internal class PlaceOrderHandler : IRequestHandler<PlaceOrder>
            {
                public Task Handle(PlaceOrder request, CancellationToken token) => Task.CompletedTask;
            }

            public abstract class HandlerBase
            {
                protected abstract void Handle(PlaceOrder command);
            }

            public class OverridingHandler : HandlerBase
            {
                protected override void Handle(PlaceOrder command) { }
            }
            """)).ShouldBeEmpty();
    }
}
