namespace Wolverine.ComplianceTests.ErrorHandling.Faults;

public class AlwaysFailsHandler
{
    public static Task Handle(OrderPlaced _) =>
        throw new InvalidOperationException("compliance failure");
}
