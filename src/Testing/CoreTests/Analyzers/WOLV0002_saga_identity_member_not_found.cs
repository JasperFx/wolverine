using Xunit;

namespace CoreTests.Analyzers;

public class WOLV0002_saga_identity_member_not_found
{
    private const string Usings = """
        using System;
        using Wolverine;
        using Wolverine.Persistence.Sagas;

        public record StartOrder(string OrderId);
        public class OrderMessage { public string? Reference; }
        public record ShipOrder(string OrderId) : OrderMessageBase;
        public record OrderMessageBase { public string? CorrelationId { get; init; } }

        // No identity member at all: Wolverine reads the SagaId envelope header, as it does for the saga's own
        // timeout messages and for replies to the messages the saga sends
        public record OrderTimeout() : TimeoutMessage(TimeSpan.FromMinutes(1));
        public record OrderReply(bool Approved);

        """;

    private static async Task<string[]> diagnosticsFor(string source)
    {
        var diagnostics = await AnalyzerHarness.DiagnosticsFor(Usings + source);
        return diagnostics.Select(x => x.Describe()).ToArray();
    }

    [Fact]
    public async Task names_a_member_the_message_does_not_have()
    {
        var diagnostics = await AnalyzerHarness.DiagnosticsFor(Usings + """
            public class OrderSaga : Saga
            {
                public string? Id { get; set; }

                public void Handle([SagaIdentityFrom("OrderNumber")] ShipOrder command) { }
            }
            """);

        var diagnostic = diagnostics.ShouldHaveSingleItem();
        diagnostic.Describe().ShouldBe("WOLV0002: SagaIdentityFrom(\"OrderNumber\")");
        diagnostic.GetMessage().ShouldContain("ShipOrder");
    }

    [Fact]
    public async Task the_comparison_is_case_sensitive_like_wolverines()
    {
        (await diagnosticsFor("""
            public class OrderSaga : Saga
            {
                public string? Id { get; set; }

                public void Handle([SagaIdentityFrom("orderId")] ShipOrder command) { }
            }
            """)).ShouldBe(["WOLV0002: SagaIdentityFrom(\"orderId\")"]);
    }

    [Fact]
    public async Task members_that_exist_are_fine()
    {
        (await diagnosticsFor("""
            public class OrderSaga : Saga
            {
                public string? Id { get; set; }

                public void Handle([SagaIdentityFrom(nameof(ShipOrder.OrderId))] ShipOrder command) { }

                // Inherited property
                public void Handle([SagaIdentityFrom("CorrelationId")] OrderMessageBase command) { }

                // Public field
                public void Handle([SagaIdentityFrom("Reference")] OrderMessage command) { }
            }
            """)).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_saga_message_with_no_identity_member_is_not_reported()
    {
        (await diagnosticsFor("""
            public class OrderSaga : Saga
            {
                public string? Id { get; set; }

                public static OrderSaga Start(StartOrder command) => new() { Id = command.OrderId };

                public void Handle(OrderTimeout timeout) { }
                public void Handle(OrderReply reply) { }
            }
            """)).ShouldBeEmpty();
    }
}
