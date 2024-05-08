using FluentValidation;
using Wolverine.FluentValidation.Internals;

namespace Wolverine.FluentValidation.Tests;

public class internals_tests
{
    [Fact]
    public async Task default_validation_action_throws_exception()
    {
        var validator = new Command1Validator();
        var command = new Command1();

        var result = await validator.ValidateAsync(command);

        var ex = Should.Throw<ValidationException>(() =>
        {
            var failureAction = new FailureAction<Command1>();
            failureAction.Throw(command, result.Errors);
        });

        ex.Message.ShouldBe("Validation failure on: Wolverine.FluentValidation.Tests.Command1");

        ex.Errors.Count().ShouldBe(result.Errors.Count());
    }

    [Fact]
    public void FluentValidationExecutor_execute_one_validator()
    {
        var validator = new Command1Validator();
        var command = new Command1();
        var failureAction = new FailureAction<Command1>();

        var ex = Should.Throw<ValidationException>(async () =>
        {
            await FluentValidationExecutor.ExecuteOne(validator, failureAction, command);
        });

        ex.Message.ShouldBe("Validation failure on: Wolverine.FluentValidation.Tests.Command1");

        ex.Errors.Count().ShouldBe(4);
    }

    [Fact]
    public void FluentValidationExecutor_execute_multiple_validators()
    {
        var validators = new List<IValidator<Command1>>
        {
            new Command1Validator(),
            new Command1Validator2()
        };

        var command = new Command1();
        var failureAction = new FailureAction<Command1>();


        var ex = Should.Throw<ValidationException>(async () =>
        {
            await FluentValidationExecutor.ExecuteMany(validators, failureAction, command);
        });

        ex.Message.ShouldBe("Validation failure on: Wolverine.FluentValidation.Tests.Command1");

        ex.Errors.Count().ShouldBe(5);
    }
}