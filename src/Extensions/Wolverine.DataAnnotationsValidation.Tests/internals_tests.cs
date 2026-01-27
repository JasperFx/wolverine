using Microsoft.Extensions.DependencyInjection;
using Wolverine.DataAnnotationsValidation.Internals;

namespace Wolverine.DataAnnotationsValidation.Tests;

public class internals_tests
{
    [Fact]
    public void default_validation_action_throws_exception()
    {
        var command = new Command1();
        
        var ex = Should.Throw<ValidationException>(() =>
        {
            var failureAction = new FailureAction<Command1>();
            failureAction.Throw(command, [new("failed")]);
        });

        ex.Message.ShouldBe("Validation failure on: Wolverine.DataAnnotationsValidation.Tests.Command1");

        ex.Failures.Count.ShouldBe(1);
    }

    [Fact]
    public void DataAnnotationsValidationExecutor_validate()
    {
        var command = new Command1();
        var failureAction = new FailureAction<Command1>();
        var services = new ServiceCollection().BuildServiceProvider();

        var ex = Should.Throw<ValidationException>(() =>
        {
            DataAnnotationsValidationExecutor.Validate(command, services, failureAction);
        });

        ex.Message.ShouldBe("Validation failure on: Wolverine.DataAnnotationsValidation.Tests.Command1");

        ex.Failures.Count.ShouldBe(3);
    }
}