using Wolverine.Attributes;
using Wolverine.Shims.MediatR;
using Xunit;

namespace CoreTests.Shims;

public class mediatr_usage : IntegrationContext
{
    public mediatr_usage(DefaultApp @default) : base(@default)
    {
    }
    
    [Fact]
    public async Task response_is_returned_from_invoke_async()
    {
        var response = await Host.MessageBus().InvokeAsync<Response>(
            new RequestWithResponse("response-test"));

        response.ShouldNotBeNull();
        response.Data.ShouldBe("passed: response-test");
    }

    [Fact]
    public async Task response_type_is_correct()
    {
        var response = await Host.MessageBus().InvokeAsync<Response>(
            new RequestWithResponse("type-test"));

        response.ShouldBeOfType<Response>();
    }
    
    [Fact]
    public async Task invoke_mediatr_handler_with_response()
    {
        var response = await Host.MessageBus().InvokeAsync<Response>(
            new RequestWithResponse("test"));

        response.ShouldNotBeNull();
        response.Data.ShouldBe("passed: test");
        response.ProcessedBy.ShouldBe("MediatR");
    }

    [Fact]
    public async Task invoke_mediatr_handler_without_response()
    {
        // Should not throw
        await Host.InvokeAsync(new RequestWithoutResponse("test"));
    }

    [Fact]
    public async Task mediatr_handler_can_return_cascading_message()
    {
        // Reset static state
        CascadingMessageHandler.ReceivedData = null;

        // Invoke the message and wait for cascading messages to be processed
        await Host.InvokeAsync(new CascadingMessage("cascade-test"));

        // Verify the cascading message was handled
        CascadingMessageHandler.ReceivedData.ShouldBe("cascade-test");
    }
}

public static class CascadingMessageHandler
{
    public static string? ReceivedData { get; set; }

    [WolverineHandler]
    public static void Handle(CascadingMessage message)
    {
        ReceivedData = message.Data;
    }
}

public record RequestWithResponse(string Data) : IRequest<Response>;
public record Response(string Data, string ProcessedBy);

public record RequestWithoutResponse(string Data) : IRequest;


public record RequestCascade(string Data) : IRequest<CascadingMessage>;
public record CascadingMessage(string Data);

public record RequestAdditionFromService(int number) : IRequest<int>;


public interface IAdditionService
{
    int Process(int number);
}

public class AdditionService : IAdditionService
{
    public int Process(int number)
    {
        return number + 1;
    }

}

public class RequestWithResponseHandler : IRequestHandler<RequestWithResponse, Response>
{
    public Task<Response> Handle(RequestWithResponse request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new Response($"passed: {request.Data}", "MediatR"));
    }
}

public class RequestWithoutResponseHandler : IRequestHandler<RequestWithoutResponse>
{
    public Task Handle(RequestWithoutResponse request, CancellationToken cancellationToken)
    {
        // Just process and return
        return Task.CompletedTask;
    }
}


public class RequestCascadeHandler : IRequestHandler<RequestCascade, CascadingMessage>
{
    public async Task<CascadingMessage> Handle(RequestCascade request, CancellationToken cancellationToken)
    {
        return new CascadingMessage(request.Data);
    }
}

public class RequestAdditionHandler(IAdditionService service) : IRequestHandler<RequestAdditionFromService, int>
{
    public async Task<int> Handle(RequestAdditionFromService request, CancellationToken cancellationToken)
    {
        return service.Process(request.number);
    }

}

