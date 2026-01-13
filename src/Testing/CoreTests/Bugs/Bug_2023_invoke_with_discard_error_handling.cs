using Microsoft.Extensions.Hosting;
using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_2023_invoke_with_discard_error_handling
{
    [Fact]
    public async Task should_throw_the_exception_from_invoke()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // By default, this should NOT apply to inline executions (e.g. InvokeAsync),
                // and an exception inside an inline execution should simply be surfaced.
                // However, as of 3.13.0, this configuration causes the error not to be thrown.
                opts.OnAnyException()
                    .Discard()
                    .And((runtime, context, ex) => {
                        // Do some application-specific error handling here...
                        return new ValueTask();
                    });
            }).StartAsync();

        var bus = host.MessageBus();

        await Should.ThrowAsync<Exception>(async () =>
        {
            await bus.InvokeAsync(new Request());
        });
        
        
    }
}

public class RequestHandler {
/*
    This is the exception that should appear to the user, but it does not.
    This should not be caught by the Wolverine error handling because according
    to the documentation,

        `When using IMessageBus.InvokeAsync() to execute a message inline,
        only the `Retry` and `Retry With Cooldown` error policies are applied
        to the execution automatically.`

    So, the `Discard/And` error policies should NOT be applied automatically,
    and the error should be thrown (which was the case in 3.9.1, and got broken
    by a commit in 3.13.0).
*/
    public string Handle(Request request) 
        => throw new Exception(@"User-facing error message that should appear to the user.");
}

public class Request { }